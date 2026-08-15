using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace Srm.Diagnostics.Kernel;

// resource-access-audit-logging: `srm audit`向けのリアルタイムETW観測。
// KernelCpuStackSampler（同じくこのプロジェクト、Microsoft.Diagnostics.Tracing.
// TraceEventパッケージを共有）が採用済みの「ETLファイルへ書き出してから後で解析する」
// 固定時間サンプリング方式ではなく、対象アプリの生存期間中ずっと動かし続ける必要が
// あるため、リアルタイムセッション（TraceEventSession(sessionName)、ファイル名を
// 渡さないコンストラクタ）+ session.Source.Process() のイベント購読方式を使う。
//
// セッション名: KernelCpuStackSamplerはKernelTraceEventParser.KernelSessionName
// （予約された"NT Kernel Logger"）を使うが、この名前はシステム全体で同時に1つしか
// 使えない（Windows 7以前の制約）。Windows 8以降はEnableKernelProviderに任意の
// セッション名を渡すと「システムトレースプロバイダ」モックになり複数のカーネル
// セッションを同時に持てる（TraceEventSession.cs内のOperatingSystemVersion.
// AtLeast(62)分岐、microsoft/perfview本体ソースで確認済み）。SRMの対象OSは
// Windows 10/11のみ（README参照）なのでこの制約に該当せず、`srm audit`は
// `srm diag --stacktrace`と衝突しない専用のセッション名を使う。
//
// 【実機未検証】この実装全体（EnableKernelProviderの引数、FileIOCreate/TcpIpConnect/
// TcpIpConnectIPV6イベントの発火タイミングと正確なフィールド内容、Stop()呼び出しで
// Source.Process()が実際にブロック解除されるまでの遅延）は、Microsoft.Diagnostics.
// Tracing.TraceEventの公開ソース（GitHub microsoft/perfview）を参照して実装した
// ものであり、実機での動作確認はまだ行っていない。tasks.md セクション1（実機PoC）を
// 参照。
public sealed class AuditTraceCollector : IDisposable
{
    public sealed record FileAccessObserved(int Pid, string FilePath, DateTime TimestampUtc);

    public sealed record NetworkConnectObserved(int Pid, System.Net.IPAddress RemoteAddress, int RemotePort, DateTime TimestampUtc);

    public event Action<FileAccessObserved>? FileAccess;
    public event Action<NetworkConnectObserved>? NetworkConnect;

    private readonly TraceEventSession _session;
    private readonly Thread _processingThread;

    // 対象アプリが子プロセスを生成するたびに呼び出し元（Srm.Cli側、JobObjectManager.
    // TryGetProcessIdsのポーリング結果）がSetTrackedPidsで更新する。参照の入れ替えは
    // アトミックなのでロック無しでイベントハンドラ側から安全に読める（volatile）。
    private volatile HashSet<int> _trackedPids;
    private bool _disposed;

    private AuditTraceCollector(TraceEventSession session, HashSet<int> initialTrackedPids)
    {
        _session = session;
        _trackedPids = initialTrackedPids;
        _processingThread = new Thread(RunProcessingLoop) { IsBackground = true, Name = "srm-audit-etw" };
    }

    public static AuditTraceCollector Start(string appName, int rootPid)
    {
        if (!(TraceEventSession.IsElevated() ?? false))
            throw new InvalidOperationException("srm audit のETWセッション開始には管理者権限が必要です");

        var sessionName = $"SRM-Audit-{appName}-{Guid.NewGuid():N}";
        var session = new TraceEventSession(sessionName) { StopOnDispose = true };

        try
        {
            // 実機検証で判明: TraceEventSession.Sourceへの初回アクセスは（カーネルセッション名
            // 以外の任意名の場合）暗黙にEnsureStarted()でセッションを起動してしまい、その後の
            // EnableKernelProviderが「セッションは最初に一度だけ有効化できる」という内部チェック
            // （IsValidSession）に必ず引っかかって例外を投げる。EnableKernelProviderを
            // .Sourceへのアクセスより先に呼ぶ必要がある（microsoft/perfview
            // TraceEventSession.cs、Sourceプロパティのgetter参照）。
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.FileIOInit | KernelTraceEventParser.Keywords.NetworkTCPIP);

            var collector = new AuditTraceCollector(session, new HashSet<int> { rootPid });

            session.Source.Kernel.FileIOCreate += collector.OnFileIOCreate;
            session.Source.Kernel.TcpIpConnect += collector.OnTcpIpConnect;
            session.Source.Kernel.TcpIpConnectIPV6 += collector.OnTcpIpConnectIPV6;

            collector._processingThread.Start();
            return collector;
        }
        catch
        {
            // EnableKernelProvider/購読設定の途中で失敗した場合、ここでDisposeしないと
            // 名前付きセッションがOS側に残り続ける（実機検証で複数回リークするのを確認済み）。
            session.Dispose();
            throw;
        }
    }

    public void SetTrackedPids(IEnumerable<int> pids) => _trackedPids = new HashSet<int>(pids);

    private void RunProcessingLoop()
    {
        try
        {
            _session.Source.Process();
        }
        catch
        {
            // Dispose()経由のStop()による正常終了時にも内部で例外が飛ぶことがある
            // （KernelCpuStackSamplerが採用している既存パターンと同じくベストエフォートで無視する）。
        }
    }

    private void OnFileIOCreate(FileIOCreateTraceData e)
    {
        if (!_trackedPids.Contains(e.ProcessID)) return;
        FileAccess?.Invoke(new FileAccessObserved(e.ProcessID, e.FileName, e.TimeStamp));
    }

    private void OnTcpIpConnect(TcpIpConnectTraceData e)
    {
        if (!_trackedPids.Contains(e.ProcessID)) return;
        NetworkConnect?.Invoke(new NetworkConnectObserved(e.ProcessID, e.daddr, e.dport, e.TimeStamp));
    }

    private void OnTcpIpConnectIPV6(TcpIpV6ConnectTraceData e)
    {
        if (!_trackedPids.Contains(e.ProcessID)) return;
        NetworkConnect?.Invoke(new NetworkConnectObserved(e.ProcessID, e.daddr, e.dport, e.TimeStamp));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _session.Stop(); } catch { /* ベストエフォート */ }
        _processingThread.Join(TimeSpan.FromSeconds(5));
        _session.Dispose();
    }
}

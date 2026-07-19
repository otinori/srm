using Srm.Runtime.Sandbox;

namespace Srm.Runtime.Operations;

// DC-021のreview_trigger解決: srm diag --stacktraceのTier2専用入口
// （FileTransferOperation/ProcessMonitorOperationと同じパターン）。Tier1は
// ホストから対象プロセスへ直接ETWセッションを張れるため、この経路を経由せず
// DiagCommand.RunStackTraceForApp内で従来通りKernelCpuStackSamplerを直接呼ぶ。
public class StackTraceOperation
{
    // 【実機で判明・重要】Tier2ゲストVM内でのETWカーネルセッション（Profile
    // キーワード、CPUサンプリング）は、Tier1（ホスト上、通常は要求した採取
    // 時間+数秒程度で完了）と比べて著しく遅い。実機計測では、3秒間の採取
    // 要求に対しリクエスト発行から結果が書き込まれるまで、対象プロセスが
    // アイドル（サンプル0件）の場合で約110秒、CPUを使い切るビジーループ
    // （4896サンプル採取）の場合で約14分かかった。機構自体は正しく動作する
    // （実機でCLR/ntdllのフレームを含む妥当な結果を確認済み）が、Windows
    // Sandboxのネスト仮想化環境ではハードウェアパフォーマンスカウンタ
    // ベースのCPUサンプリング（Profileキーワード）にVM層のオーバーヘッドが
    // 大きく乗ると見られる（未確定、DC-021のreview_trigger参照）。この
    // 特性を踏まえ、Tier1よりはるかに長いタイムアウトバッファを確保する。
    private static readonly TimeSpan Tier2EtwOverheadBuffer = TimeSpan.FromMinutes(20);

    public StackTraceResultModel Capture(string appName, int durationMs, int top)
    {
        var app = RunningAppRegistry.Find(appName)
            ?? throw new SrmOperationException(
                $"実行中のアプリが見つかりません: {appName}\nsrm list で実行中のアプリを確認してください", 1);

        if (app.Tier != 2)
            throw new SrmOperationException($"StackTraceOperationはTier2専用です: {appName}", 1);

        if (string.IsNullOrWhiteSpace(app.ControlDir))
            throw new SrmOperationException($"{appName} のcontrol_dirが記録されていません", 2);

        var control = new SandboxControlChannel(app.ControlDir);
        var requestId = Guid.NewGuid().ToString();
        control.WriteRequest("stacktrace", new StackTraceRequestModel { RequestId = requestId, DurationMs = durationMs, Top = top });

        var timeout = TimeSpan.FromMilliseconds(durationMs) + Tier2EtwOverheadBuffer;
        return control.WaitForResult<StackTraceResultModel>("stacktrace", requestId, timeout)
            ?? throw new SrmOperationException($"ゲスト内でのスタックトレース採取がタイムアウトしました: {appName}", 5);
    }
}

using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace Srm.Diagnostics.Kernel;

// DC-016（claude-code CLIをAppContainer内で-p実行するとカーネルモードCPUを消費し
// 続ける未解決バグ）の調査で、`wpr.exe -stop`がこの環境ではCOMアパートメント
// エラー（0x80010106）で失敗し、手動でのWPR/WPAトレース採取が実質使えないことが
// 判明した。wpr.exeという外部プロセスに頼らず、.NETのETW API
// （Microsoft.Diagnostics.Tracing.TraceEvent、PerfView/dotnet-traceが内部で
// 使っているのと同じライブラリ）でカーネルCPUサンプリング＋スタックトレース
// 採取を`srm`自体に内蔵し、同じ失敗を構造的に回避する。
//
// 採取したサンプルは「スタックの先頭（実行中だったフレーム）」ごとに集計する
// 単純な flat profile とし、WPAのような詳細なコールツリー分析は行わない
// （DC-016が必要としている「具体的な関数・モジュールの特定」には十分）。
public class KernelCpuStackSampler
{
    public sealed record FrameSample(string Frame, int Count, double Percent);

    public sealed record NamedPipeSample(string Name, int Count);

    public sealed record CaptureResult(
        bool Success, string? Reason, int ProcessId, int TotalSamples,
        IReadOnlyList<FrameSample> TopFrames, IReadOnlyList<NamedPipeSample> NamedPipesCreated);

    public CaptureResult Capture(int targetProcessId, TimeSpan duration, int topN = 20)
    {
        if (!(TraceEventSession.IsElevated() ?? false))
            return Fail(targetProcessId, "カーネルETWセッションの開始には管理者権限が必要です");

        var etlPath = Path.Combine(Path.GetTempPath(), $"srm-kernel-cpu-{Guid.NewGuid():N}.etl");
        try
        {
            using (var session = new TraceEventSession(KernelTraceEventParser.KernelSessionName, etlPath))
            {
                session.StopOnDispose = true;
                session.EnableKernelProvider(
                    KernelTraceEventParser.Keywords.Profile |
                    KernelTraceEventParser.Keywords.ImageLoad |
                    KernelTraceEventParser.Keywords.Process |
                    KernelTraceEventParser.Keywords.FileIOInit,
                    stackCapture: KernelTraceEventParser.Keywords.Profile);

                Thread.Sleep(duration);
            }

            return Analyze(etlPath, targetProcessId, topN);
        }
        catch (Exception ex)
        {
            return Fail(targetProcessId, $"カーネルトレースの採取に失敗しました: {ex.Message}");
        }
        finally
        {
            TryDelete(etlPath);
            TryDelete(Path.ChangeExtension(etlPath, ".etlx"));
        }
    }

    private static CaptureResult Analyze(string etlPath, int targetProcessId, int topN)
    {
        using var traceLog = TraceLog.OpenOrConvert(etlPath);

        var process = traceLog.Processes.LastProcessWithID(targetProcessId);
        if (process == null)
            return Fail(targetProcessId, "トレース内に対象プロセスのイベントが見つかりません（採取時間が短すぎるか、対象プロセスが既に終了している可能性があります）");

        var debug = Environment.GetEnvironmentVariable("SRM_DIAG_DEBUG") == "1";
        if (debug)
        {
            var eventsInProcess = process.EventsInProcess.ToList();
            var byType = eventsInProcess.GroupBy(e => e.GetType().Name).Select(g => $"{g.Key}={g.Count()}");
            Console.Error.WriteLine($"[debug] pid={targetProcessId} events={eventsInProcess.Count} types: {string.Join(", ", byType)}");
        }

        // 1パス目: シンボル解決前に、実際にリーフフレームとして出現したコード
        // アドレスだけを集計する。対象プロセス自体（claude.exe等）が持つ
        // モジュールも含め、ロード済みモジュール全部のシンボルを解決すると
        // 実機で数分〜かかることが分かったため（多くはリーフフレームに一切
        // 出現しないモジュール）、実際に出現したモジュールだけに絞って
        // 2パス目でシンボル解決する。
        var counts = new Dictionary<CodeAddressIndex, int>();
        var addressByIndex = new Dictionary<CodeAddressIndex, TraceCodeAddress>();
        var modulesSeen = new Dictionary<ModuleFileIndex, TraceModuleFile>();
        var namedPipes = new Dictionary<string, int>();
        var total = 0;

        foreach (var e in process.EventsInProcess)
        {
            if (e is SampledProfileTraceData)
            {
                var stack = e.CallStack();
                if (stack == null) continue;

                var codeAddress = stack.CodeAddress;
                var idx = codeAddress.CodeAddressIndex;
                counts[idx] = counts.GetValueOrDefault(idx) + 1;
                addressByIndex.TryAdd(idx, codeAddress);
                if (codeAddress.ModuleFile != null) modulesSeen.TryAdd(codeAddress.ModuleFile.ModuleFileIndex, codeAddress.ModuleFile);
                total++;
                continue;
            }

            // 「NtCreateNamedPipeFileがリーフフレーム上位に出る」ことは分かって
            // いても、それだけではどのパイプ名か（＝claude.exe内のどの機能が
            // 使っているか）は分からない。同じカーネルセッションでFileIOInit
            // キーワードも有効化し、Create系イベントのFileNameから名前付き
            // パイプ（\Device\NamedPipe\配下）だけを抜き出して集計する。
            // 注: DC-016の実機調査では対象プロセスにこのイベントが一件も
            // 記録されなかった（NtCreateNamedPipeFileの呼び出し自体はCPU
            // サンプルのリーフフレームとして頻出するにもかかわらず）。これは
            // 「作成が完了する前段階（セキュリティチェック等）で毎回失敗して
            // いる」ことを示唆する追加の状況証拠であり、バグではない
            // （DC-016 review_trigger参照）。
            if (e is FileIOCreateTraceData create && LooksLikeNamedPipe(create.FileName))
                namedPipes[create.FileName] = namedPipes.GetValueOrDefault(create.FileName) + 1;
        }

        if (total == 0)
            return Fail(targetProcessId, "対象プロセスのCPUサンプリングイベントが採取できませんでした");

        using var symbolReader = new SymbolReader(debug ? Console.Error : TextWriter.Null, SymbolPath.MicrosoftSymbolServerPath)
        {
            Options = SymbolReaderOptions.NoNGenSymbolCreation,
        };

        foreach (var module in modulesSeen.Values)
        {
            try { traceLog.CodeAddresses.LookupSymbolsForModule(symbolReader, module); }
            catch (Exception ex) when (debug)
            {
                Console.Error.WriteLine($"[debug] symbol lookup failed for {module.FilePath}: {ex.Message}");
            }
            catch { /* シンボル取得に失敗したモジュールはモジュール名のみで表示する（ベストエフォート） */ }
        }

        var histogram = new Dictionary<string, int>();
        foreach (var (idx, count) in counts)
        {
            var frame = DescribeFrame(addressByIndex[idx]);
            histogram[frame] = histogram.GetValueOrDefault(frame) + count;
        }

        var top = histogram
            .OrderByDescending(kv => kv.Value)
            .Take(topN)
            .Select(kv => new FrameSample(kv.Key, kv.Value, Math.Round(kv.Value * 100.0 / total, 1)))
            .ToList();

        var pipes = namedPipes
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new NamedPipeSample(kv.Key, kv.Value))
            .ToList();

        return new CaptureResult(true, null, targetProcessId, total, top, pipes);
    }

    // カーネルFileIOイベントのFileNameはNTパス（\Device\HarddiskVolumeN\...等）で
    // 出てくるため、名前付きパイプは`\Device\NamedPipe\`配下という特徴的な
    // プレフィックスで判別できる（通常ファイルのI/Oと混同しない）。
    private static bool LooksLikeNamedPipe(string? fileName) =>
        !string.IsNullOrEmpty(fileName) &&
        fileName.Contains(@"\Device\NamedPipe\", StringComparison.OrdinalIgnoreCase);

    private static string DescribeFrame(TraceCodeAddress codeAddress)
    {
        var methodName = codeAddress.FullMethodName;
        if (!string.IsNullOrEmpty(methodName)) return methodName;

        var moduleName = codeAddress.ModuleName;
        return string.IsNullOrEmpty(moduleName)
            ? $"0x{codeAddress.Address:x}"
            : $"{moduleName}!0x{codeAddress.Address:x}";
    }

    private static CaptureResult Fail(int processId, string reason) => new(false, reason, processId, 0, [], []);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ベストエフォート */ }
    }
}

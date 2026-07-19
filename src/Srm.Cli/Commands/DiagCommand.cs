using System.CommandLine;
using System.Text.Json;
using Srm.Diagnostics.Kernel;
using Srm.Runtime;
using Srm.Runtime.Diagnostics;
using Srm.Runtime.Operations;
using Srm.Runtime.Sandbox;

namespace Srm.Cli.Commands;

// DC-016（claude-code CLIをAppContainer内で-p実行するとカーネルモードCPUを消費し
// 続ける未解決バグ）の実機再調査用の診断コマンド。MCPツールとしては公開しない
// （通常のAIエージェント操作からは使わない調査専用のコマンドのため）。
public static class DiagCommand
{
    public static Command Build()
    {
        var appArg = new Argument<string?>("app", () => null,
            "診断対象のアプリ名（srm listに表示される名前。--pid指定時は不要）")
        { Arity = ArgumentArity.ZeroOrOne };
        var pidOption = new Option<int?>(
            "--pid",
            "srm管理外の任意のプロセスをPIDで直接指定する（appの代わりに使う。AppContainer隔離の" +
            "有無による挙動比較など、srm run経由でないプロセスも調査対象にしたい場合に使う）");
        var durationOption = new Option<int>(
            "--duration-ms", () => 1000, "CPU時間・ページフォールト数（または--stacktrace指定時はカーネルCPUサンプリング）を採取する経過時間（ミリ秒）");
        var stackTraceOption = new Option<bool>(
            "--stacktrace",
            "カーネルCPUサンプリング+スタックトレースを採取し、消費上位の関数/モジュールを表示する" +
            "（wpr.exeに依存せず.NET ETW APIで直接採取する。管理者権限が必要。要初回シンボルダウンロード）");
        var topOption = new Option<int>("--top", () => 20, "--stacktrace指定時に表示する上位フレーム数");
        // tier2-channel-c-mapped-folder: tier2-process-monitorのCLI入口
        // （design.mdのOpen Questionsで例示された"srm diag <app> --watch"の形をそのまま採用）。
        var watchOption = new Option<bool>(
            "--watch", "継続的にプロセス一覧・リソース使用状況をポーリングする（Tier2専用、tier2-process-monitor）");
        var intervalOption = new Option<int>("--interval-ms", () => 1000, "--watch指定時のスナップショット取得間隔（ミリ秒）");
        var countOption = new Option<int>("--count", () => 5, "--watch指定時に取得するスナップショット数");
        var cmd = new Command(
            "diag", "実行中のアプリのCPU時間内訳（ユーザー/カーネル）・ページフォールト数を短時間サンプリングする（DC-016調査用）")
        {
            appArg, pidOption, durationOption, stackTraceOption, topOption, watchOption, intervalOption, countOption,
        };

        cmd.SetHandler(context =>
        {
            var appName = context.ParseResult.GetValueForArgument(appArg);
            var pid = context.ParseResult.GetValueForOption(pidOption);
            var durationMs = context.ParseResult.GetValueForOption(durationOption);
            var stackTrace = context.ParseResult.GetValueForOption(stackTraceOption);
            var top = context.ParseResult.GetValueForOption(topOption);
            var watch = context.ParseResult.GetValueForOption(watchOption);
            var intervalMs = context.ParseResult.GetValueForOption(intervalOption);
            var count = context.ParseResult.GetValueForOption(countOption);

            if (string.IsNullOrWhiteSpace(appName) && pid == null)
            {
                Console.Error.WriteLine("app名または--pidのいずれかを指定してください。");
                Environment.Exit(1);
                return;
            }

            if (watch)
            {
                RunWatch(appName!, intervalMs, count);
                return;
            }

            if (pid != null)
            {
                RunForPid(pid.Value, stackTrace, durationMs, top);
                return;
            }

            if (stackTrace)
            {
                RunStackTraceForApp(appName!, durationMs, top);
                return;
            }

            try
            {
                var result = new DiagOperation().Execute(appName!, durationMs);
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                if (!result.Success) Environment.Exit(1);
            }
            catch (SrmOperationException ex)
            {
                Console.Error.WriteLine(ex.Message);
                Environment.Exit(ex.ExitCode);
            }
        });

        return cmd;
    }

    private static void RunWatch(string appName, int intervalMs, int count)
    {
        try
        {
            var results = new ProcessMonitorOperation().Watch(appName, intervalMs, count);
            foreach (var result in results)
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (SrmOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Environment.Exit(ex.ExitCode);
        }
    }

    // srm run経由で起動していない任意のプロセス（AppContainer隔離無しでのDC-016比較
    // 再現等）も、PIDさえ分かれば同じ診断ロジックで調査できるようにする。
    // RunningAppRegistry/DiagOperationのTier振り分けは経由しない
    // （--pid指定時は常にホストから直接サンプリングする、実質Tier1相当の経路のみ）。
    private static void RunForPid(int pid, bool stackTrace, int durationMs, int top)
    {
        if (stackTrace)
        {
            var stackResult = new KernelCpuStackSampler().Capture(pid, TimeSpan.FromMilliseconds(durationMs), top);
            Console.WriteLine(JsonSerializer.Serialize(stackResult, new JsonSerializerOptions { WriteIndented = true }));
            if (!stackResult.Success) Environment.Exit(1);
            return;
        }

        var result = new DiagCollector().Handle(pid, new DiagRequestModel { RequestId = Guid.NewGuid().ToString(), SampleWindowMs = durationMs });
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        if (!result.Success) Environment.Exit(1);
    }

    private static void RunStackTraceForApp(string appName, int durationMs, int top)
    {
        var app = RunningAppRegistry.Find(appName);
        if (app == null)
        {
            Console.Error.WriteLine($"実行中のアプリが見つかりません: {appName}\nsrm list で実行中のアプリを確認してください");
            Environment.Exit(1);
            return;
        }

        if (app.Tier == 2)
        {
            // DC-021のreview_trigger解決: ETWセッションはサンプリング対象と同じ
            // カーネル上でしか動かせないため、ホストからは開始できない。ゲスト内
            // `--nested`自身に採取させ、control/経由で結果を受け取る
            // （StackTraceOperation、tier2-channel-c-mapped-folderの汎用
            // request/resultプリミティブを使う）。実機計測でTier2のETW
            // セッションはTier1よりはるかに遅く（採取要求3秒に対し実測で
            // 最大約14分かかった、StackTraceOperationのコメント参照）、
            // 見た目上コマンドがフリーズしたように見えるため事前に警告する。
            Console.Error.WriteLine(
                "[注意] Tier2でのカーネルスタックトレース採取はTier1よりはるかに時間がかかります" +
                "（実機で数分〜十数分程度）。応答が無いように見えても正常です。しばらくお待ちください。");
            try
            {
                var tier2Result = new StackTraceOperation().Capture(appName, durationMs, top);
                Console.WriteLine(JsonSerializer.Serialize(tier2Result, new JsonSerializerOptions { WriteIndented = true }));
                if (!tier2Result.Success) Environment.Exit(1);
            }
            catch (SrmOperationException ex)
            {
                Console.Error.WriteLine(ex.Message);
                Environment.Exit(ex.ExitCode);
            }
            return;
        }

        var result = new KernelCpuStackSampler().Capture(app.Pid, TimeSpan.FromMilliseconds(durationMs), top);
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        if (!result.Success) Environment.Exit(1);
    }
}

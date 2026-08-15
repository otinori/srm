using System.CommandLine;
using Srm.Diagnostics.Kernel;
using Srm.PolicyEngine;
using Srm.Runtime;
using Srm.Runtime.Diagnostics;
using Srm.Runtime.Operations;

namespace Srm.Cli.Commands;

// resource-access-audit-logging: `srm audit`は`srm run`と違いブロックせず、対象アプリの
// ファイル/ネットワークアクセス試行をETWで観測して記録する（AuditOperation/
// AuditTraceCollector参照）。srm runのようにバックグラウンドで起動して即座に戻る設計
// ではなく、srm diag --watchに近いフォアグラウンド・ブロッキングのコマンドとして実装する
// （対象アプリの生存期間中ずっと同じプロセスがETWイベントを消費し続ける必要があるため）。
public static class AuditCommand
{
    public static Command Build(string policiesDir)
    {
        var policyArg = new Argument<string>("policy", "ポリシー名 (例: claude-code) または .yaml への絶対パス");
        var scopeOption = new Option<string[]>(
            "--scope",
            "読み取りアクセスを観測する対象パス（複数指定可、--scope A --scope B）。省略時は" +
            "ポリシーの application.working_directory を使う。")
        { AllowMultipleArgumentsPerToken = false };
        var noIntegrityOption = new Option<bool>("--no-integrity-check", "整合性検証をスキップする（開発時のみ）");

        var cmd = new Command(
            "audit",
            "ブロックせずファイルシステム/ネットワークへのアクセス試行を記録する（Tier1専用、隔離はしない）")
        {
            policyArg, scopeOption, noIntegrityOption,
        };

        cmd.SetHandler(context =>
        {
            var policyName = context.ParseResult.GetValueForArgument(policyArg);
            var scopeArgs = context.ParseResult.GetValueForOption(scopeOption) ?? Array.Empty<string>();
            var noIntegrity = context.ParseResult.GetValueForOption(noIntegrityOption);

            Run(policiesDir, policyName, scopeArgs, noIntegrity);
        });

        return cmd;
    }

    private static void Run(string policiesDir, string policyName, string[] scopeArgs, bool noIntegrity)
    {
        AuditOperationResult opResult;
        try
        {
            // --scope省略時はapplication.working_directoryを既定値にする。AuditOperation.
            // Executeは確定済みのscopePathsを受け取る設計にしてあるため、既定値の決定に
            // 必要な分だけ、ここで一度ポリシーを読む（AuditOperation内部でも整合性検証込みで
            // 読み直すため二重読み込みにはなるが、シンプルさを優先する）。
            var scopePaths = scopeArgs.Length > 0 ? scopeArgs : ResolveDefaultScope(policiesDir, policyName);

            opResult = new AuditOperation().Execute(policiesDir, policyName, scopePaths, noIntegrity);
        }
        catch (SrmOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Environment.Exit(ex.ExitCode);
            return;
        }

        Console.Error.WriteLine(
            "[警告] srm audit は隔離を行いません。ファイルシステムへの広い読み取りアクセスと、" +
            "ネットワークへの無制限のアウトバウンド接続を許可した状態で対象アプリを実行し、" +
            "アクセス試行を記録するだけです。信頼できないアプリ・バイナリの観測には、" +
            "VM境界のあるTier2ポリシーとの併用を強く推奨します（現時点ではTier1のみ対応）。");

        var policy = opResult.Policy;
        var classifier = new AuditVerdictClassifier(policy.Filesystem, policy.Network);
        var logger = opResult.Logger;

        var allowedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wouldBlockPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wouldBlockHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileEventCount = 0;
        var networkEventCount = 0;
        var countLock = new object();

        using var collector = AuditTraceCollector.Start(policy.Name, opResult.Pid);

        collector.FileAccess += e =>
        {
            var verdict = classifier.ClassifyPath(e.FilePath);
            lock (countLock)
            {
                fileEventCount++;
                (verdict == AuditVerdict.Allowed ? allowedPaths : wouldBlockPaths).Add(e.FilePath);
            }
            logger.Info("audit_fs", new { pid = e.Pid, path = e.FilePath, verdict = verdict.ToString() });
        };

        collector.NetworkConnect += e =>
        {
            var verdict = classifier.ClassifyHost(e.RemoteAddress);
            var hostLabel = $"{e.RemoteAddress}:{e.RemotePort}";
            lock (countLock)
            {
                networkEventCount++;
                (verdict == AuditVerdict.Allowed ? allowedHosts : wouldBlockHosts).Add(hostLabel);
            }
            logger.Info("audit_net", new { pid = e.Pid, remote = hostLabel, verdict = verdict.ToString() });
        };

        Console.WriteLine($"監視を開始しました: {policy.Name} (PID: {opResult.Pid})。Ctrl+Cで停止します。");

        var stopRequested = false;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopRequested = true;
        };

        while (!stopRequested)
        {
            collector.SetTrackedPids(
                JobObjectManager.TryGetProcessIds(opResult.JobName, out var pids) && pids.Length > 0
                    ? pids.Append(opResult.Pid)
                    : new[] { opResult.Pid });

            try
            {
                if (System.Diagnostics.Process.GetProcessById(opResult.Pid).HasExited) break;
            }
            catch (ArgumentException)
            {
                break; // 対象プロセスが既に終了している
            }

            Thread.Sleep(500);
        }

        // Ctrl+Cで止めた場合はプロセスツリーごと後始末する（対象プロセスが自然終了して
        // ループを抜けた場合はここに来ない）。
        if (stopRequested)
        {
            try
            {
                var proc = System.Diagnostics.Process.GetProcessById(opResult.Pid);
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(5000);
            }
            catch { /* 既に終了している場合はベストエフォート */ }
        }

        var summary = new AuditSummary
        {
            FileEventCount = fileEventCount,
            NetworkEventCount = networkEventCount,
            AllowedPaths = allowedPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
            WouldBlockPaths = wouldBlockPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
            AllowedHosts = allowedHosts.OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToList(),
            WouldBlockHosts = wouldBlockHosts.OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToList(),
        };
        logger.Info("srm audit 終了", summary);

        Console.WriteLine();
        Console.WriteLine($"=== audit サマリー: {policy.Name} ===");
        Console.WriteLine($"ファイルアクセス: {summary.FileEventCount}件（許可済み {summary.AllowedPaths.Count}件 / 未許可 {summary.WouldBlockPaths.Count}件）");
        Console.WriteLine($"ネットワーク接続: {summary.NetworkEventCount}件（許可済み {summary.AllowedHosts.Count}件 / 未許可 {summary.WouldBlockHosts.Count}件）");
        if (summary.WouldBlockPaths.Count > 0)
        {
            Console.WriteLine("未許可のパス（srm runなら拒否されていたはず）:");
            foreach (var p in summary.WouldBlockPaths) Console.WriteLine($"  - {p}");
        }
        if (summary.WouldBlockHosts.Count > 0)
        {
            Console.WriteLine("未許可の接続先（srm runなら拒否されていたはず）:");
            foreach (var h in summary.WouldBlockHosts) Console.WriteLine($"  - {h}");
        }
        Console.WriteLine($"詳細ログ: srm logs {policy.Name}");
    }

    private static string[] ResolveDefaultScope(string policiesDir, string policyName)
    {
        var path = PolicyPathResolver.Resolve(policiesDir, policyName);
        var policy = new PolicyLoader().Load(path);
        if (string.IsNullOrWhiteSpace(policy.Application.WorkingDirectory))
        {
            Console.Error.WriteLine(
                "監視対象パスが指定されていません。--scope <path> を1つ以上指定するか、" +
                "application.working_directory が設定されたポリシーを使用してください。");
            Environment.Exit(1);
            return Array.Empty<string>();
        }
        return new[] { policy.Application.WorkingDirectory };
    }
}

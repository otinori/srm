using Srm.Runtime.Evidence;
using Srm.Runtime.Logging;
using Srm.Runtime.Sandbox;

namespace Srm.Runtime.Operations;

public class StopOperationResult
{
    public required string AppName { get; init; }
    public required int Tier { get; init; }
}

// srm stop のTier1/Tier2停止ロジック本体。元はSrm.Cli.Commands.StopCommandに直書き
// されていたものをここに移し、Console出力・Environment.Exitを行わない形にした
// （RunOperationと同じ理由: CLIとMCPの両方から再利用するため）。
public class StopOperation
{
    private static readonly TimeSpan GracefulStopTimeout = TimeSpan.FromSeconds(30);

    public StopOperationResult Execute(string appName)
    {
        if (!AdministratorChecker.IsAdministrator())
            throw new SrmOperationException(
                "srm stop には管理者権限が必要です。管理者として実行したシェルから再度実行してください。", 3);

        var app = RunningAppRegistry.Find(appName);
        if (app == null)
            throw new SrmOperationException(
                $"実行中のアプリが見つかりません: {appName}\nsrm list で実行中のアプリを確認してください", 1);

        var logger = new StructuredLogger(appName);

        if (app.Tier == 2)
            StopTier2(app, logger);
        else
            StopTier1(app, appName, logger);

        RunningAppRegistry.Unregister(appName);

        return new StopOperationResult { AppName = appName, Tier = app.Tier };
    }

    private static void StopTier1(RunningApp app, string appName, StructuredLogger logger)
    {
        try
        {
            var proc = System.Diagnostics.Process.GetProcessById(app.Pid);
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(5000);
            logger.Info("プロセス停止", new { pid = app.Pid });
        }
        catch (ArgumentException)
        {
            logger.Warn("プロセスが既に終了していました", new { pid = app.Pid });
        }

        try
        {
            WfpManager.RemoveForApp(appName);
            logger.Info("WFPフィルター削除完了", new { app = appName });
        }
        catch (Exception ex)
        {
            logger.Warn("WFPフィルターの削除に失敗しました", new { app = appName, error = ex.Message });
        }

        // channel-d-guest-mcp-bridge: --mcp-bridge-hostはsrm run自身とは別プロセス
        // （SpawnMcpBridgeHost）で、対象アプリのプロセスツリーにもJob Objectにも
        // 属さないため、上のKillだけでは終了しない。放置すると実MCPサーバー
        // 子プロセスごとオーファン化することを実機で確認済み（tasks.md task 3.6）。
        // Tier2のstopped.signal待機と違い、ブリッジホストの終了完了を待つ厳密な
        // 仕組みはまだ無いため、シグナルを送るだけのベストエフォートに留める。
        if (!string.IsNullOrWhiteSpace(app.McpControlDir))
        {
            try
            {
                new SandboxControlChannel(app.McpControlDir).SignalStop();
                logger.Info("MCPブリッジホストへ停止シグナルを送信", new { control_dir = app.McpControlDir });
            }
            catch (Exception ex)
            {
                logger.Warn("MCPブリッジホストへの停止シグナル送信に失敗しました", new { control_dir = app.McpControlDir, error = ex.Message });
            }
        }
    }

    // DC-010: stop.signal→stopped.signalの正常終了フローを試み、ゲスト内でevidenceの
    // flushが完了してからVMを閉じさせる。タイムアウト時はホスト側のコンテナ
    // プロセスを強制終了する。
    private static void StopTier2(RunningApp app, StructuredLogger logger)
    {
        if (!string.IsNullOrWhiteSpace(app.ControlDir))
        {
            var control = new SandboxControlChannel(app.ControlDir);
            control.SignalStop();

            if (control.WaitForStopped(GracefulStopTimeout))
                logger.Info("Tier2正常終了（stopped.signal受信）", new { pid = app.Pid });
            else
                logger.Warn("stopped.signalがタイムアウトしたため強制終了します", new { pid = app.Pid });
        }
        else
        {
            logger.Warn("control_dirが記録されていないため強制終了します", new { pid = app.Pid });
        }

        // stopped.signalはゲスト内対象アプリの終了を示すだけで、Windows Sandbox VM
        // 自体は自動的には閉じない（ゲストにVMの電源を落とす手段を実装していないため）。
        // 正常終了・タイムアウトのどちらの経路でも、ホスト側のWindowsSandbox.exe
        // プロセスを終了させて初めてVMウィンドウが閉じる（実機で再現・確認済み。
        // 以前はタイムアウト経路にしかこの呼び出しが無かった）。
        KillHostProcess(app.Pid, logger);

        // VM停止確定後、outboxの内容を検疫パイプラインへ通す（DC-013）。マップフォルダは
        // ホスト側の実ディレクトリなので、VMが閉じた後でも中身はそのまま読み取れる。
        QuarantineOutbox(app, logger);
    }

    private static void QuarantineOutbox(RunningApp app, StructuredLogger logger)
    {
        if (string.IsNullOrWhiteSpace(app.RunId) || string.IsNullOrWhiteSpace(app.OutboxDir))
            return;

        try
        {
            var store = new EvidenceQuarantineStore(scanFile: WindowsDefenderScanner.Scan);
            var run = store.Quarantine(app.Name, app.RunId, app.OutboxDir);
            logger.Info("evidence_quarantined", new
            {
                app = app.Name,
                run_id = app.RunId,
                file_count = run.FileCount,
                violation_count = run.Violations.Count,
            });
        }
        catch (Exception ex)
        {
            logger.Warn("evidenceの検疫に失敗しました", new { app = app.Name, run_id = app.RunId, error = ex.Message });
        }
    }

    private static void KillHostProcess(int pid, StructuredLogger logger)
    {
        try
        {
            var proc = System.Diagnostics.Process.GetProcessById(pid);
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(5000);
            logger.Info("Windows Sandboxホストプロセスを強制終了", new { pid });
        }
        catch (ArgumentException)
        {
            logger.Warn("プロセスが既に終了していました", new { pid });
        }
    }
}

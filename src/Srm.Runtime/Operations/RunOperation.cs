using Srm.PolicyEngine;
using Srm.PolicyEngine.Models;
using Srm.PolicyIntegrity;
using Srm.Runtime.Logging;
using Srm.Runtime.Sandbox;

namespace Srm.Runtime.Operations;

public class RunOperationResult
{
    public required string AppName { get; init; }
    public required int Tier { get; init; }
    public required int Pid { get; init; }
    public string? RunId { get; init; }

    // Tier2のみ。ready.signal待機がタイムアウトした場合true（起動処理自体は失敗させず、
    // 呼び出し元に警告として伝える。既存CLIの挙動を踏襲）。
    public bool ReadySignalTimedOut { get; init; }
}

// srm run のTier1/Tier2起動ロジック本体。元はSrm.Cli.Commands.RunCommandに直書きされて
// いたものをここに移し、Console出力・Environment.Exitを行わない形にしたことで、CLIと
// MCPの両方から同じロジックを再利用できるようにする（--nested/--quota-monitorという
// ゲスト内・監視専用の内部呼び出しモードはCLI固有の起動経路であり続けるためRunCommand側に残す）。
public class RunOperation
{
    public RunOperationResult Execute(string policiesDir, string policyName, bool noIntegrityCheck, bool redirectStdioToNul = false)
    {
        if (!AdministratorChecker.IsAdministrator())
            throw new SrmOperationException(
                "srm run には管理者権限が必要です。管理者として実行したシェルから再度実行してください。", 3);

        var path = PolicyPathResolver.Resolve(policiesDir, policyName);

        if (!noIntegrityCheck)
        {
            try
            {
                new IntegrityVerifier().Verify(path);
            }
            catch (IntegrityException ex)
            {
                throw new SrmOperationException($"[整合性エラー] {ex.Message}", 2);
            }
        }

        var policy = new PolicyLoader().Load(path);

        var validation = new PolicyValidator().Validate(policy);
        if (!validation.IsValid)
        {
            var detail = string.Join("\n", validation.Errors.Select(e => $"  - {e}"));
            throw new SrmOperationException($"ポリシーが無効です: {policyName}\n{detail}", 1);
        }

        // Home エディションでの Tier2 指定はここで NotSupportedException が飛ぶ。
        // 既存CLIの挙動と同様、SrmOperationExceptionには変換せずそのまま呼び出し元に伝播させる。
        WindowsEditionDetector.ThrowIfTier2OnHome(policy.Tier);

        return policy.Tier == 2
            ? RunTier2(policy, path, policyName)
            : RunTier1(policy, policyName, path, redirectStdioToNul);
    }

    private static RunOperationResult RunTier1(PolicyModel policy, string policyName, string path, bool redirectStdioToNul)
    {
        var logger = new StructuredLogger(policy.Name, policy.Logging.RetentionDays);
        logger.Info("srm run 開始", new { policy = policyName, tier = policy.Tier });

        using var sidFactory = AppContainerSidFactory.Create($"srm-{policy.Name}");
        var aclMgr = new AclManager();
        aclMgr.GrantAccess(policy.Filesystem.AllowPaths, sidFactory.Sid);
        aclMgr.GrantExecuteAccess(policy.Application.Executable, sidFactory.Sid);
        logger.Info("ACL設定完了", new { paths = policy.Filesystem.AllowPaths.Select(p => p.Path), executable = policy.Application.Executable });

        // using にしない: srm run は起動して即リターンする設計だが、WFPフィルターは
        // 対象アプリが動作している間ずっと有効でなければならない。ここで Dispose すると
        // フィルターが直後に削除され、ネットワーク制限がほぼ無効化されてしまう
        // （2026-07-01発見の重大バグ）。片付けは srm stop の WfpManager.RemoveForApp が行う。
        WfpManager.Install(policy.Network, WfpIdentity.FromPackageSid(sidFactory.Sid), policy.Name);
        logger.Info("WFPフィルター設定完了", new { allow_hosts = policy.Network.AllowHosts });

        // 名前を付けてJob Objectを作成する: WindowGuard（DC-017）が別プロセス
        // （Srm.Mcpのsend_key等の呼び出し）からOpenJobObjectで再オープンし、
        // 対象ウィンドウの所有PIDがこのJob Object配下に実在するかを確認できるようにする。
        var jobName = $"srm-job-{policy.Name}";
        using var job = JobObjectManager.Create(policy.Process.MaxProcesses, jobName);

        // channel-d-guest-mcp-bridge: Tier1にはTier2のMappedFolderに相当するものが
        // 無いため、Channel Dのcontrolフォルダは新規のACL付与フォルダとして用意する
        // （design.md「祖先Traverseは共有・実アクセスはポリシー個別」ではなく、
        // 既存のallow_pathsと同じ単純なAppContainer SID直接付与でよい。共有grantが
        // 必要になるほど頻繁にフォルダを切り替える設計ではないため）。対象プロセス
        // 起動前に用意する必要がある: SRM_MCP_CONTROL_DIR/SRM_MCP_SERVERSを対象
        // プロセス自身の環境変数として渡し、ゲスト側スタブ（対象アプリの子プロセスとして
        // 起動される想定）が継承できるようにするため。
        Dictionary<string, string>? mcpEnv = null;
        if (policy.Mcp.AllowServers.Count > 0)
        {
            var mcpControlDir = Tier1ControlPaths.ControlDir(policy.Name);
            Directory.CreateDirectory(mcpControlDir);
            aclMgr.GrantAccess(
                new List<AllowedPath> { new() { Path = mcpControlDir, Access = "rw" } },
                sidFactory.Sid);

            var guestStubPath = Path.Combine(AppContext.BaseDirectory, "Srm.McpBridgeGuest.exe");
            if (File.Exists(guestStubPath))
                aclMgr.GrantExecuteAccess(guestStubPath, sidFactory.Sid);
            else
                logger.Warn("MCPブリッジ: ゲスト側スタブが見つかりません（実行権を付与できません）", new { expected_path = guestStubPath });

            mcpEnv = new Dictionary<string, string>
            {
                ["SRM_MCP_CONTROL_DIR"] = mcpControlDir,
                ["SRM_MCP_SERVERS"] = string.Join(',', policy.Mcp.AllowServers.Select(s => s.Name)),
            };
        }

        var launcher = new AppContainerLauncher();
        var result = launcher.Launch(policy, sidFactory.Sid, redirectStdioToNul: redirectStdioToNul, extraEnvironmentVariables: mcpEnv);
        logger.Info("プロセス起動", new { pid = result.ProcessId, exe = policy.Application.Executable });

        // 診断用（2026-07-01・一時的）: SRM_DIAG_NEW_CONSOLE=1のときはJob Object割り当て
        // 失敗（CREATE_NEW_CONSOLEと衝突する既知の問題）を無視して切り分ける。
        try
        {
            job.AssignProcess(result.ProcessHandle);
        }
        catch (System.ComponentModel.Win32Exception) when (Environment.GetEnvironmentVariable("SRM_DIAG_NEW_CONSOLE") == "1")
        {
            logger.Warn("[診断] Job Object割り当てに失敗しましたが無視して続行します", new { });
        }

        RunningAppRegistry.Register(new RunningApp
        {
            Name = policy.Name,
            Pid = result.ProcessId,
            StartedAt = DateTimeOffset.Now,
            PolicyPath = path,
            JobName = jobName,
            McpControlDir = mcpEnv?["SRM_MCP_CONTROL_DIR"],
        });

        if (mcpEnv != null)
            SpawnMcpBridgeHost(policy.Name, mcpEnv["SRM_MCP_CONTROL_DIR"], path, logger);

        return new RunOperationResult { AppName = policy.Name, Tier = 1, Pid = result.ProcessId };
    }

    // Tier2（Windows Sandbox）での起動。DC-010: Sandbox境界の内側にAppContainer隔離を
    // 重ねる多層防御構成。ホスト側は.wsb生成・ACL事前付与・VM起動・ready.signal待機の
    // オーケストレーションのみを行い、実際の対象アプリ起動はゲスト内の
    // `srm.exe run --nested` (RunCommand.RunNested) が担う。
    private static RunOperationResult RunTier2(PolicyModel policy, string policyPath, string policyName)
    {
        var logger = new StructuredLogger(policy.Name, policy.Logging.RetentionDays);
        logger.Info("srm run 開始（Tier2）", new { policy = policyName, tier = policy.Tier });

        // DC-012: srmはツールチェーンの取得・バージョン解決を行わない。ホスト側の
        // キャッシュに事前配置されていなければ、VMを起動する前にエラーで終了する
        // （起動後にゲスト内で気づいてもリトライ手段が無く、2分間の起動待ちを
        // 無駄にするだけのため）。
        foreach (var t in policy.Provision.Toolchain)
        {
            var cacheDir = ToolchainCache.EntryDir(t.Name, t.Version);
            if (!Directory.Exists(cacheDir))
            {
                logger.Error("provisionツールチェーン未配置", new { name = t.Name, version = t.Version, expected_path = cacheDir });
                throw new SrmOperationException(
                    $"[provisionエラー] ツールチェーンキャッシュが見つかりません: {cacheDir}\n" +
                    $"  {t.Name} {t.Version} 用のポータブル配布物(.zip)を事前に配置してください（DC-012: srmは取得を代行しません）。", 5);
            }
        }

        var runId = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var toolingDir = AppContext.BaseDirectory;

        var launcher = new SandboxLauncher();
        SandboxRunResult result;
        try
        {
            result = launcher.Launch(policy, policyPath, runId, toolingDir, TimeSpan.FromMinutes(2));
        }
        catch (Exception ex)
        {
            logger.Error("Windows Sandbox起動失敗", new { error = ex.Message });
            throw new SrmOperationException($"[Tier2起動エラー] Windows Sandboxの起動に失敗しました: {ex.Message}", 4);
        }

        var readyTimedOut = !result.Control.IsReady;
        if (readyTimedOut)
            logger.Warn("ready.signalタイムアウト", new { run_id = runId });

        RunningAppRegistry.Register(new RunningApp
        {
            Name = policy.Name,
            Pid = result.HostProcess.Id,
            StartedAt = DateTimeOffset.Now,
            PolicyPath = policyPath,
            Tier = 2,
            ControlDir = result.Paths.Control,
            RunId = runId,
            OutboxDir = result.Paths.Outbox,
            InputDir = result.Paths.Input,
        });

        logger.Info("Windows Sandbox起動", new { run_id = runId, host_pid = result.HostProcess.Id });

        // evidence.max_outbox_bytesが指定されている場合のみ、outbox監視用のプロセスを
        // 別途detachedで起動する（DC-013）。srm run自体は起動後すぐ戻る設計のため、
        // 実行中ずっとポーリングし続ける役目は同じsrm.exeの別インスタンスに担わせる
        // （DC-008と同じ「IPC・デーモンなし」の思想。Windows Sandbox自体もこの形で
        // 起動している）。起動元プロセス（srm.exe / Srm.Mcp のどちらでも良い）自身の
        // パスをそのまま再起動対象にするため、呼び出し元のEnvironment.ProcessPathを使う。
        if (policy.Evidence.MaxOutboxBytes is > 0)
            SpawnQuotaMonitor(policy.Name, result.Paths.Control, result.Paths.Outbox, policy.Evidence.MaxOutboxBytes.Value, logger);

        // channel-d-guest-mcp-bridge: Tier2は既存のMappedFolder（control/）をそのまま
        // 使う。ゲスト側スタブはRunNested側で別途起動する必要がある（tasks.md 4.2）。
        if (policy.Mcp.AllowServers.Count > 0)
            SpawnMcpBridgeHost(policy.Name, result.Paths.Control, policyPath, logger);

        return new RunOperationResult
        {
            AppName = policy.Name,
            Tier = 2,
            Pid = result.HostProcess.Id,
            RunId = runId,
            ReadySignalTimedOut = readyTimedOut,
        };
    }

    // channel-d-guest-mcp-bridge: SpawnQuotaMonitorと全く同じパターン
    // （design.mdのDecision「新しいIPC機構は作らず既存の`srm.exe`背景プロセス起動
    // 手段を再利用する」）。Tier1/Tier2どちらからも同じ形で呼べる。
    private static void SpawnMcpBridgeHost(string appName, string controlDir, string policyPath, StructuredLogger logger)
    {
        try
        {
            var exePath = Path.Combine(AppContext.BaseDirectory, "srm.exe");
            if (!File.Exists(exePath))
            {
                logger.Warn("MCPブリッジホストの起動をスキップしました（srm.exeが見つかりません）", new { expected_path = exePath });
                return;
            }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add(appName);
            psi.ArgumentList.Add("--mcp-bridge-host");
            psi.ArgumentList.Add("--control-dir");
            psi.ArgumentList.Add(controlDir);
            psi.ArgumentList.Add("--mcp-policy-path");
            psi.ArgumentList.Add(policyPath);
            System.Diagnostics.Process.Start(psi);
            logger.Info("MCPブリッジホストプロセスを起動", new { control_dir = controlDir });
        }
        catch (Exception ex)
        {
            logger.Warn("MCPブリッジホストプロセスの起動に失敗しました", new { error = ex.Message });
        }
    }

    private static void SpawnQuotaMonitor(string appName, string controlDir, string outboxDir, long maxBytes, StructuredLogger logger)
    {
        try
        {
            // Environment.ProcessPath は使わない: このメソッドは Srm.Cli（srm.exe）と
            // Srm.Mcp の両方から呼ばれうるが、`run <app> --quota-monitor ...` という
            // CLI引数を解釈できるのは srm.exe だけ（Srm.MpcはCLIパーサーを持たない）。
            // 配布レイアウト（bin/srm.exe と bin/Srm.Mcp.exe が同じフォルダに並ぶ）を前提に、
            // 呼び出し元プロセスの実行ファイルパスに関わらず常に srm.exe を明示的に探す。
            var exePath = Path.Combine(AppContext.BaseDirectory, "srm.exe");
            if (!File.Exists(exePath))
            {
                logger.Warn("outboxクォータ監視の起動をスキップしました（srm.exeが見つかりません）", new { expected_path = exePath });
                return;
            }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add(appName);
            psi.ArgumentList.Add("--quota-monitor");
            psi.ArgumentList.Add("--control-dir");
            psi.ArgumentList.Add(controlDir);
            psi.ArgumentList.Add("--outbox-dir");
            psi.ArgumentList.Add(outboxDir);
            psi.ArgumentList.Add("--max-bytes");
            psi.ArgumentList.Add(maxBytes.ToString());
            System.Diagnostics.Process.Start(psi);
            logger.Info("outboxクォータ監視プロセスを起動", new { max_bytes = maxBytes });
        }
        catch (Exception ex)
        {
            logger.Warn("outboxクォータ監視プロセスの起動に失敗しました", new { error = ex.Message });
        }
    }
}

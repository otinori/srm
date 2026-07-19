using System.CommandLine;
using Srm.Diagnostics.Kernel;
using Srm.PolicyEngine;
using Srm.PolicyEngine.Models;
using Srm.Runtime;
using Srm.Runtime.Diagnostics;
using Srm.Runtime.Interaction;
using Srm.Runtime.Logging;
using Srm.Runtime.Operations;
using Srm.Runtime.Sandbox;

namespace Srm.Cli.Commands;

public static class RunCommand
{
    public static Command Build(string policiesDir)
    {
        var policyArg = new Argument<string?>("policy", () => null,
            "ポリシー名 (例: claude-code) または .yaml への絶対パス（--nested指定時は不要）")
        { Arity = ArgumentArity.ZeroOrOne };
        var noIntegrityOption = new Option<bool>("--no-integrity-check", "整合性検証をスキップする（開発時のみ）");
        var nestedOption = new Option<bool>("--nested", "Tier2ゲスト内から呼び出される内部モード（直接使用しない）");
        var controlDirOption = new Option<string?>("--control-dir", "Tier2ゲスト内のcontrolフォルダパス（--nested指定時必須）");
        var quotaMonitorOption = new Option<bool>("--quota-monitor", "Tier2 outboxクォータ監視の内部モード（直接使用しない）");
        var outboxDirOption = new Option<string?>("--outbox-dir", "監視対象のoutboxフォルダパス（--quota-monitor指定時必須）");
        var maxBytesOption = new Option<long?>("--max-bytes", "outboxの上限バイト数（--quota-monitor指定時必須）");
        var mcpBridgeHostOption = new Option<bool>("--mcp-bridge-host", "channel-d-guest-mcp-bridgeのホスト側ブリッジ内部モード（直接使用しない）");
        var mcpPolicyPathOption = new Option<string?>("--mcp-policy-path", "対象ポリシーファイルの絶対パス（--mcp-bridge-host指定時必須）");

        var cmd = new Command("run", "ポリシーに従ってアプリをサンドボックス内で起動する")
        {
            policyArg, noIntegrityOption, nestedOption, controlDirOption,
            quotaMonitorOption, outboxDirOption, maxBytesOption,
            mcpBridgeHostOption, mcpPolicyPathOption,
        };

        cmd.SetHandler(context =>
        {
            var policyName = context.ParseResult.GetValueForArgument(policyArg);
            var noIntegrity = context.ParseResult.GetValueForOption(noIntegrityOption);
            var nested = context.ParseResult.GetValueForOption(nestedOption);
            var controlDir = context.ParseResult.GetValueForOption(controlDirOption);
            var quotaMonitor = context.ParseResult.GetValueForOption(quotaMonitorOption);
            var outboxDir = context.ParseResult.GetValueForOption(outboxDirOption);
            var maxBytes = context.ParseResult.GetValueForOption(maxBytesOption);
            var mcpBridgeHost = context.ParseResult.GetValueForOption(mcpBridgeHostOption);
            var mcpPolicyPath = context.ParseResult.GetValueForOption(mcpPolicyPathOption);

            if (nested)
            {
                RunNested(controlDir);
                return;
            }

            if (quotaMonitor)
            {
                RunQuotaMonitor(policyName, controlDir, outboxDir, maxBytes);
                return;
            }

            if (mcpBridgeHost)
            {
                McpBridgeHostCommand.Run(policyName, controlDir, mcpPolicyPath);
                return;
            }

            if (string.IsNullOrWhiteSpace(policyName))
            {
                Console.Error.WriteLine("ポリシー名を指定してください。例: srm run claude-code");
                Environment.Exit(1);
                return;
            }

            try
            {
                var result = new RunOperation().Execute(policiesDir, policyName, noIntegrity);

                if (result.Tier == 2)
                {
                    if (result.ReadySignalTimedOut)
                        Console.Error.WriteLine("[Tier2起動警告] ゲスト内での起動確認(ready.signal)がタイムアウトしました。srm list で状態を確認してください。");
                    Console.WriteLine($"起動しました: {result.AppName} (Tier2, RunID: {result.RunId}, Host PID: {result.Pid})");
                }
                else
                {
                    Console.WriteLine($"起動しました: {result.AppName} (PID: {result.Pid})");
                }
            }
            catch (SrmOperationException ex)
            {
                Console.Error.WriteLine(ex.Message);
                Environment.Exit(ex.ExitCode);
            }
        });

        return cmd;
    }

    // Tier2ゲスト内の `srm.exe run --nested` から呼ばれる。control-dir/policy.yaml を
    // 読み込み、既存のAppContainerLauncher/JobObjectManagerをそのまま使って対象アプリを
    // 起動する（DC-010: ゲスト内エージェントを新規開発せずsrm.exe自身を再利用する）。
    // ホスト専用のステップ（管理者権限チェック・整合性検証・WFP登録）はゲスト側では
    // 不要なため行わない（DC-010: ACLは事前にホストが付与済み。DC-011: ネットワーク制御は
    // Sandbox境界側で行う）。ゲスト内でのみ起動するCLI固有の経路のため、RunOperationには
    // 抽出せずここに残す（MCP経由で直接呼ばれることはない）。
    private static void RunNested(string? controlDir)
    {
        if (string.IsNullOrWhiteSpace(controlDir))
        {
            Console.Error.WriteLine("--nested には --control-dir の指定が必須です。");
            Environment.Exit(1);
            return;
        }

        var control = new SandboxControlChannel(controlDir);
        var policyPath = Path.Combine(controlDir, "policy.yaml");

        var loader = new PolicyLoader();
        var policy = loader.Load(policyPath);

        var logger = new StructuredLogger(policy.Name, policy.Logging.RetentionDays);
        logger.Info("srm run 開始（nested）", new { policy = policy.Name, tier = policy.Tier });

        if (policy.Provision.Steps.Count > 0)
        {
            var provisionSourceDir = SandboxConfigGenerator.GuestProvisionDir(Path.GetDirectoryName(controlDir)!);
            try
            {
                new Provisioner().Run(policy.Provision, provisionSourceDir, policy.Application.WorkingDirectory, msg =>
                {
                    logger.Info("provision", new { message = msg });
                    Console.WriteLine($"[provision] {msg}");
                });
            }
            catch (Exception ex)
            {
                // ready.signalを送らずここで終了する。ホスト側は既存の
                // ready.signalタイムアウト経路で気づき、nested.logで原因を確認できる
                // （LogonCommandの障害調査で確立した既存のパターンをそのまま踏襲）。
                logger.Error("provision失敗", new { error = ex.Message });
                Console.Error.WriteLine($"[provisionエラー] {ex.Message}");
                Environment.Exit(1);
                return;
            }
        }

        LaunchResult result;

        // tier2-guest-network-egress: DC-010は常にネスト先をAppContainerでラップする
        // 設計だったが、DC-016でAppContainerトークンがBun（Claude Code）の名前付き
        // パイプ生成を壊す（NtCreateNamedPipeFileが既定DACLで失敗し無限リトライする）
        // ことが実機で確定し、srm側からの修正手段が無いと判明した。tier2.app_container:
        // false はそのopt-outで、AppContainerLauncherを経由せず直接起動する。
        // fs/プロセスの隔離保証はTier1相当から後退し、VM境界（Windows Sandbox自体の
        // 破棄可能性）のみになる（restricted-account-app-isolation実装前は特に）。
        // ネットワーク遮断はAppContainer SIDではなくAppPath（実行ファイルパス）を
        // 条件にしたWfpManagerで維持する（proposal.md/design.md参照）。
        if (policy.Tier2.AppContainer)
        {
            using var sidFactory = AppContainerSidFactory.Create($"srm-{policy.Name}");
            var aclMgr = new AclManager();
            aclMgr.GrantAccess(policy.Filesystem.AllowPaths, sidFactory.Sid);
            aclMgr.GrantExecuteAccess(policy.Application.Executable, sidFactory.Sid);
            logger.Info("ACL設定完了", new { paths = policy.Filesystem.AllowPaths.Select(p => p.Path), executable = policy.Application.Executable });

            // DC-015（DC-011改訂）: Windows SandboxのゲストネットワークはHyper-V Firewallは
            // おろか通常のWindows Defender Firewall（vEthernet(Default Switch)へ
            // インターフェーススコープしたInbound/Outboundルール双方）でも一切フィルタ
            // できないことが実機PoCで判明したため、Tier1と同じAppContainer SIDベースの
            // WfpManagerをゲスト内でそのまま再利用する（DC-011が当初明示的に却下していた
            // 方式。ゲスト内でSYSTEM権限相当を奪われればフィルタ自体を解除されうる弱さを
            // 受け入れる）。ゲストも独立したOSインスタンスでWFPエンジンを持つため、
            // Sandbox境界向けのバリエーション実装は不要でTier1のロジックがそのまま動く。
            WfpManager.Install(policy.Network, WfpIdentity.FromPackageSid(sidFactory.Sid), policy.Name);
            logger.Info("WFPフィルター設定完了（nested）", new { allow_hosts = policy.Network.AllowHosts });

            // channel-d-guest-mcp-bridge: ゲスト側スタブはホストのtoolingDirが
            // 読み取り専用MappedFolderとしてマウントされたもの（DC-010）にsrm.exe自身と
            // 並んで配置される（tools/package.ps1が両方を同じartifacts\publishへ
            // publishする）ため、AppContext.BaseDirectory（このsrm.exe--nested自身の
            // 実行ディレクトリ）からTier1と全く同じ相対パスで見つかる。ホスト側の
            // --mcp-bridge-hostはRunOperation.RunTier2がすでに同じcontrolDirへ向けて
            // 起動している（Tier1と違い、こちらはゲスト内で個別に起動する必要はない）。
            Dictionary<string, string>? mcpEnv = null;
            if (policy.Mcp.AllowServers.Count > 0 && !string.IsNullOrWhiteSpace(controlDir))
            {
                var guestStubPath = Path.Combine(AppContext.BaseDirectory, "Srm.McpBridgeGuest.exe");
                if (File.Exists(guestStubPath))
                    aclMgr.GrantExecuteAccess(guestStubPath, sidFactory.Sid);
                else
                    logger.Warn("MCPブリッジ: ゲスト側スタブが見つかりません（実行権を付与できません）", new { expected_path = guestStubPath });

                mcpEnv = new Dictionary<string, string>
                {
                    ["SRM_MCP_CONTROL_DIR"] = controlDir,
                    ["SRM_MCP_SERVERS"] = string.Join(',', policy.Mcp.AllowServers.Select(s => s.Name)),
                };
            }

            var launcher = new AppContainerLauncher();
            result = launcher.Launch(policy, sidFactory.Sid, extraEnvironmentVariables: mcpEnv);
        }
        else
        {
            // restricted-account-app-isolation: 完全に無防備な`LaunchWithoutAppContainer`
            // ではなく、専用アカウント+Low Integrity Levelによる書き込みバリアを使う
            // （design.md参照。DC-023で実機検証済み）。WFPの識別子もAppPath
            // （実行ファイルパスのみに基づく粗い識別）から、この専用アカウントの
            // UserSidへ切り替える（tier2-guest-network-egress DC-022 task 5、
            // より正確な識別子に対応）。
            var account = RestrictedAccountManager.EnsureAccount(policy.Name);
            var aclMgr = new AclManager();
            var sharedGroupSid = new System.Security.Principal.SecurityIdentifier(account.SharedGroupSid);

            aclMgr.GrantAccess(policy.Filesystem.AllowPaths, account.Sid, account.SharedGroupSid, sharedGroupSid);
            aclMgr.GrantExecuteAccess(policy.Application.Executable, account.Sid, account.SharedGroupSid, sharedGroupSid);
            foreach (var allowedPath in policy.Filesystem.AllowPaths)
                aclMgr.SetLowIntegrityLabel(allowedPath.Path);
            logger.Info("ACL設定完了（restricted-account-app-isolation）", new
            {
                paths = policy.Filesystem.AllowPaths.Select(p => p.Path),
                executable = policy.Application.Executable,
                account = account.Username,
            });

            WfpManager.Install(policy.Network, WfpIdentity.FromUserSid(account.Sid), policy.Name);
            logger.Info("WFPフィルター設定完了（nested、restricted-account-app-isolation）", new { allow_hosts = policy.Network.AllowHosts });

            // channel-d-guest-mcp-bridge: このアカウントはLow ILで動くため、
            // AppContainerブランチと同様の明示的な実行権付与に加え、controlDirへの
            // ACL付与とLow ILラベル付けの両方が必要（ラベルが無いと既定Medium ILの
            // ままとなり、Low ILプロセスからmcp-request.jsonを書き込めない）。
            Dictionary<string, string>? mcpEnv = null;
            if (policy.Mcp.AllowServers.Count > 0 && !string.IsNullOrWhiteSpace(controlDir))
            {
                var guestStubPath = Path.Combine(AppContext.BaseDirectory, "Srm.McpBridgeGuest.exe");
                if (File.Exists(guestStubPath))
                    aclMgr.GrantExecuteAccess(guestStubPath, account.Sid, account.SharedGroupSid, sharedGroupSid);
                else
                    logger.Warn("MCPブリッジ: ゲスト側スタブが見つかりません（実行権を付与できません）", new { expected_path = guestStubPath });

                aclMgr.GrantAccess(
                    new List<AllowedPath> { new() { Path = controlDir, Access = "rw" } },
                    account.Sid, account.SharedGroupSid, sharedGroupSid);
                aclMgr.SetLowIntegrityLabel(controlDir);

                mcpEnv = new Dictionary<string, string>
                {
                    ["SRM_MCP_CONTROL_DIR"] = controlDir,
                    ["SRM_MCP_SERVERS"] = string.Join(',', policy.Mcp.AllowServers.Select(s => s.Name)),
                };
            }

            var launcher = new RestrictedAccountLauncher();
            result = launcher.Launch(policy, account, mcpEnv);
        }

        logger.Info("プロセス起動", new { pid = result.ProcessId, exe = policy.Application.Executable, app_container = policy.Tier2.AppContainer });

        // チャネルB（DC-017）のScenarioExecutorがTier1と同じ「Job Object配下PID」の
        // ガードロジックをそのまま再利用できるよう、ここでも名前付きJob Objectにする
        // （同一プロセス内で作成・使用するため本来は無名でも足りるが、Tier1側の
        // JobObjectManager.TryGetProcessIdsをそのまま呼べる形に揃えることを優先した）。
        var jobName = $"srm-job-nested-{policy.Name}";
        using var job = JobObjectManager.Create(policy.Process.MaxProcesses, jobName);

        try { job.AssignProcess(result.ProcessHandle); }
        catch (System.ComponentModel.Win32Exception) { /* ベストエフォート */ }

        control.SignalReady();
        Console.WriteLine($"起動しました（nested）: {policy.Name} (PID: {result.ProcessId})");

        // チャネルB（DC-017）: ホスト側のrun_scenarioはsrm_run完了（ready.signal）後に
        // 呼ばれる想定のため、起動直後に一度だけ確認するのではなく、stop.signalを
        // 待つ間ずっとscenario.jsonの出現を監視する。
        // tier2-channel-a-focus-guarantee: チャネルA（ホスト側から直接RDPウィンドウへ
        // SendInputする経路）はゲスト内のフォーカス状態を検証できないため、送信直前に
        // ホストがfocus-request.jsonを書き、ここで検出してゲスト内（同一セッション）で
        // 確実にフォーカスを設定する。
        // tier2-channel-c-mapped-folder: tier2-file-transferは新規capabilityの
        // ためscenario/focus/diagのような型付き専用パラメータを追加せず、
        // `kind`文字列で登録する汎用ハンドラ辞書を使う（design.md Decision 2）。
        control.BlockUntilStop(
            onScenarioDetected: () => RunScenario(control, controlDir, jobName, result.ProcessId, logger),
            onFocusRequestDetected: req => HandleFocusRequest(control, policy.Name, jobName, result.ProcessId, req, logger),
            onDiagRequestDetected: req => HandleDiagRequest(control, result.ProcessId, req, logger),
            genericRequestHandlers: new Dictionary<string, Action<string>>
            {
                ["file-transfer"] = requestId => HandleFileTransferRequest(control, policy, controlDir, requestId, logger),
                ["process-monitor"] = requestId => HandleProcessMonitorRequest(control, jobName, result.ProcessId, requestId, logger),
                ["stacktrace"] = requestId => HandleStackTraceRequest(control, result.ProcessId, requestId, logger),
            });

        try
        {
            var proc = System.Diagnostics.Process.GetProcessById(result.ProcessId);
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(5000);
            logger.Info("プロセス停止（nested）", new { pid = result.ProcessId });
        }
        catch { /* 既に終了している場合はベストエフォート */ }

        try
        {
            WfpManager.RemoveForApp(policy.Name);
            logger.Info("WFPフィルター削除完了（nested）", new { app = policy.Name });
        }
        catch (Exception ex)
        {
            logger.Warn("WFPフィルターの削除に失敗しました（nested）", new { app = policy.Name, error = ex.Message });
        }

        control.SignalStopped();
    }

    // チャネルB（オートパイロット、DC-017）: BlockUntilStopのコールバックからscenario.json
    // 出現時に一度だけ呼ばれる。
    private static void RunScenario(
        SandboxControlChannel control, string controlDir, string jobName, int appProcessId, StructuredLogger logger)
    {
        var scenario = control.ReadScenario();
        if (scenario == null)
        {
            logger.Warn("scenario.jsonの読み込みに失敗しました", new { });
            return;
        }

        logger.Info("シナリオ実行開始", new { step_count = scenario.Steps.Count });

        var allowedPids = JobObjectManager.TryGetProcessIds(jobName, out var pids)
            ? pids.ToHashSet()
            : new HashSet<int> { appProcessId };
        allowedPids.Add(appProcessId);

        var outboxDir = SandboxConfigGenerator.GuestOutboxDir(Path.GetDirectoryName(controlDir)!);
        var result = new ScenarioExecutor().Execute(scenario, allowedPids, outboxDir);
        control.WriteScenarioResult(result);

        logger.Info("シナリオ実行完了", new { success = result.Success, step_count = result.Steps.Count });
    }

    // tier2-channel-a-focus-guarantee: BlockUntilStopのコールバックからfocus-request.json
    // のrequestIdが変わるたびに呼ばれる。ホスト側が直接RDPウィンドウへSendInputする前に、
    // ゲスト内（同一セッション）で確実に対象ウィンドウをフォアグラウンド化しておく。
    // 本体ロジックはFocusRequestHandler（Srm.Runtime、直接テスト可能）に委譲する。
    private static void HandleFocusRequest(
        SandboxControlChannel control, string policyName, string jobName, int appProcessId,
        FocusRequestModel request, StructuredLogger logger)
    {
        var allowedPids = JobObjectManager.TryGetProcessIds(jobName, out var pids)
            ? pids.ToHashSet()
            : new HashSet<int> { appProcessId };
        allowedPids.Add(appProcessId);

        var result = new FocusRequestHandler().Handle(policyName, allowedPids, request);
        control.WriteFocusResult(result);

        if (result.Success)
            logger.Info("フォーカス設定完了", new { request_id = request.RequestId, app = request.App });
        else
            logger.Warn("フォーカス設定に失敗しました", new { request_id = request.RequestId, reason = result.Reason });
    }

    // 診断チャネル（DC-016調査用）: BlockUntilStopのコールバックからdiag-request.jsonの
    // requestIdが変わるたびに呼ばれる。ホスト側のsrm diagが指定したサンプリング時間だけ
    // ここでブロックする（フォーカス要求と違い数百ms〜数秒かかることを前提とした
    // 一回限りの調査用操作のため、その間BlockUntilStopループが他のイベントを
    // 検出できなくても許容する）。対象は起動時に記録済みの対象アプリPID固定で、
    // フォーカス要求と違いウィンドウ解決は不要。
    private static void HandleDiagRequest(
        SandboxControlChannel control, int appProcessId, DiagRequestModel request, StructuredLogger logger)
    {
        var result = new DiagCollector().Handle(appProcessId, request);
        control.WriteDiagResult(result);

        if (result.Success)
            logger.Info("診断サンプリング完了", new { request_id = request.RequestId, kernel_time_percent = result.KernelTimePercent });
        else
            logger.Warn("診断サンプリングに失敗しました", new { request_id = request.RequestId, reason = result.Reason });
    }

    // tier2-channel-c-mapped-folder: BlockUntilStopの汎用ハンドラ辞書から
    // "file-transfer"のrequestIdが変わるたびに呼ばれる。本体ロジックは
    // FileTransferRequestHandler（Srm.Runtime、直接テスト可能）に委譲する。
    private static void HandleFileTransferRequest(
        SandboxControlChannel control, PolicyModel policy, string controlDir, string requestId, StructuredLogger logger)
    {
        var request = control.TryReadRequest<FileTransferRequestModel>("file-transfer");
        if (request == null || request.RequestId != requestId) return;

        var guestRoot = Path.GetDirectoryName(controlDir)!;
        var result = new FileTransferRequestHandler().Handle(policy, guestRoot, request);
        control.WriteResult("file-transfer", result);

        if (result.Success)
            logger.Info("ファイル転送完了", new { request_id = request.RequestId, direction = request.Direction.ToString() });
        else
            logger.Warn("ファイル転送に失敗しました", new { request_id = request.RequestId, reason = result.Reason });
    }

    // tier2-channel-c-mapped-folder: BlockUntilStopの汎用ハンドラ辞書から
    // "process-monitor"の新しいrequestIdを検出したときに一度だけ呼ばれる。
    // file-transferと違い1回で完結せず、design.md Decision 5の「継続的な
    // スナップショット上書き」を実現するため、ここではバックグラウンドスレッドを
    // 起動して即座に戻る（BlockUntilStopの本体ループをブロックしないため。
    // ブロックすると他のイベント検出・stop.signal監視が止まってしまう）。
    private static void HandleProcessMonitorRequest(
        SandboxControlChannel control, string jobName, int appProcessId, string requestId, StructuredLogger logger)
    {
        var request = control.TryReadRequest<ProcessMonitorRequestModel>("process-monitor");
        if (request == null || request.RequestId != requestId) return;

        logger.Info("プロセス監視を開始", new { request_id = requestId, interval_ms = request.IntervalMs });

        var thread = new Thread(() => RunProcessMonitorLoop(control, jobName, appProcessId, request, logger))
        {
            IsBackground = true,
        };
        thread.Start();
    }

    // stop.signalが出るか、host側が新しいrequestIdで上書きする（＝この監視セッションが
    // 打ち切られた）まで、指定間隔でmonitor-result.jsonへ上書きし続ける。
    private static void RunProcessMonitorLoop(
        SandboxControlChannel control, string jobName, int appProcessId, ProcessMonitorRequestModel request, StructuredLogger logger)
    {
        var intervalMs = Math.Max(request.IntervalMs, 250);
        while (!control.IsStopRequested)
        {
            if (control.TryReadRequestId("process-monitor") != request.RequestId)
                return;

            var allowedPids = JobObjectManager.TryGetProcessIds(jobName, out var pids)
                ? pids.ToHashSet()
                : new HashSet<int> { appProcessId };
            allowedPids.Add(appProcessId);

            var result = new ProcessMonitorHandler().Handle(request.RequestId, allowedPids);
            control.WriteResult("process-monitor", result);

            if (!result.Success)
                logger.Warn("プロセス監視のスナップショット取得に失敗しました", new { request_id = request.RequestId, reason = result.Reason });

            Thread.Sleep(intervalMs);
        }
    }

    // DC-021のreview_trigger解決: BlockUntilStopの汎用ハンドラ辞書から
    // "stacktrace"のrequestIdが変わるたびに呼ばれる。ETWセッションはサンプリング
    // 対象と同じカーネル上でしか動かせないため、ホスト側からは開始できず、
    // ゲスト内`--nested`自身がKernelCpuStackSampler（Tier1と全く同じ実装、
    // Srm.Diagnostics.Kernel）を呼び出す。診断チャネル（HandleDiagRequest）と
    // 同じく、採取時間中（既定1秒、シンボル解決を含めるとさらに長くなりうる）
    // BlockUntilStopループが他のイベントを検出できなくても許容する
    // （一回限りの調査用操作のため）。
    private static void HandleStackTraceRequest(
        SandboxControlChannel control, int appProcessId, string requestId, StructuredLogger logger)
    {
        var request = control.TryReadRequest<StackTraceRequestModel>("stacktrace");
        if (request == null || request.RequestId != requestId) return;

        logger.Info("カーネルスタックトレース採取開始", new { request_id = requestId, duration_ms = request.DurationMs });

        var capture = new KernelCpuStackSampler().Capture(appProcessId, TimeSpan.FromMilliseconds(request.DurationMs), request.Top);
        var result = new StackTraceResultModel
        {
            RequestId = requestId,
            Success = capture.Success,
            Reason = capture.Reason,
            ProcessId = capture.ProcessId,
            TotalSamples = capture.TotalSamples,
            TopFrames = capture.TopFrames.Select(f => new StackTraceFrameModel { Frame = f.Frame, Count = f.Count, Percent = f.Percent }).ToList(),
            NamedPipesCreated = capture.NamedPipesCreated.Select(p => new NamedPipeSampleModel { Name = p.Name, Count = p.Count }).ToList(),
        };
        control.WriteResult("stacktrace", result);

        if (result.Success)
            logger.Info("カーネルスタックトレース採取完了", new { request_id = requestId, total_samples = result.TotalSamples });
        else
            logger.Warn("カーネルスタックトレース採取に失敗しました", new { request_id = requestId, reason = result.Reason });
    }

    // Tier2実行中、別プロセスとして起動されるoutboxクォータ監視の内部モード（DC-013、6.6）。
    // stopped.signal/stop.signalが現れる（=正規の停止フローが既に進行中）まで
    // ポーリングし、閾値超過を検出したらstop.signalを書いて自身は終了する。
    // 実際の停止処理（対象アプリのkill・stopped.signal送出）はゲスト内nested側が担う。
    // RunOperation.SpawnQuotaMonitorが必ずsrm.exeを対象に起動するため、この経路も
    // CLI固有のままで良い（MCP経由で直接呼ばれることはない）。
    private static void RunQuotaMonitor(string? appName, string? controlDir, string? outboxDir, long? maxBytes)
    {
        if (string.IsNullOrWhiteSpace(controlDir) || string.IsNullOrWhiteSpace(outboxDir) || maxBytes is not > 0)
        {
            Console.Error.WriteLine("--quota-monitor には --control-dir/--outbox-dir/--max-bytes の指定が必須です。");
            Environment.Exit(1);
            return;
        }

        var logger = new StructuredLogger(string.IsNullOrWhiteSpace(appName) ? "unknown" : appName);
        var control = new SandboxControlChannel(controlDir);
        var pollInterval = TimeSpan.FromSeconds(5);

        while (!control.IsStopped && !control.IsStopRequested)
        {
            if (OutboxQuotaMonitor.IsOverQuota(outboxDir, maxBytes.Value))
            {
                var size = OutboxQuotaMonitor.ComputeSize(outboxDir);
                logger.Warn("outboxクォータ超過のため早期終了させます", new { outbox_dir = outboxDir, size_bytes = size, max_bytes = maxBytes.Value });
                control.SignalStop();
                return;
            }

            Thread.Sleep(pollInterval);
        }
    }
}

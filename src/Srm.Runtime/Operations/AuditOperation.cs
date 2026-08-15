using Srm.PolicyEngine;
using Srm.PolicyEngine.Models;
using Srm.PolicyIntegrity;
using Srm.Runtime.Logging;

namespace Srm.Runtime.Operations;

public class AuditOperationResult
{
    public required PolicyModel Policy { get; init; }
    public required int Pid { get; init; }
    public required string JobName { get; init; }
    public required StructuredLogger Logger { get; init; }
}

// resource-access-audit-logging: `srm audit`のTier1専用オーケストレーション。
// RunOperation.RunTier1とほぼ同じ手順を踏むが、決定的に3点異なる。
//   (1) WfpManagerを一切呼ばない。ネットワークを拒否せず、対象アプリが実際に
//       どこへ接続しようとするかをETWで観測する方針のため（design.md「WFPネット
//       イベント購読ではなくAppContainerのinternetClientケーパビリティ強制付与+
//       ETW観測」の決定。当初案のFwpmNetEventSubscribe4は不採用に変更した）。
//   (2) allow_pathsではなく呼び出し元が指定したscopePathsに、AclManager.
//       GrantReadAccessForAudit で読み取りのみを付与する（書き込みは許可しない、
//       design.md Non-Goals）。
//   (3) ETWでのイベント収集そのものはここでは行わない。Srm.Runtimeは
//       Microsoft.Diagnostics.Tracing.TraceEventに依存させない既存の設計方針
//       （KernelCpuStackSamplerはSrm.Diagnostics.Kernelにあり、Srm.Cliからのみ
//       呼ばれる。Srm.Runtimeは経由しない）をそのまま踏襲し、収集はSrm.Cli側の
//       AuditCommandがSrm.Diagnostics.Kernel.AuditTraceCollectorを直接使って行う。
//
// srm runと違いバックグラウンドで起動して即座に戻る設計にはしていない
// （RunningAppRegistryへの登録もしない）。ETWでの継続的な観測は、対象アプリの
// 生存期間中ずっと同じプロセス（呼び出し元のsrm.exe自身）がイベントを消費し続ける
// 必要があるため、`srm audit`はAuditCommand側でフォアグラウンド・ブロッキングの
// コマンドとして実装される想定（`srm diag --watch`に近いUX）。
public class AuditOperation
{
    public AuditOperationResult Execute(string policiesDir, string policyName, IReadOnlyList<string> scopePaths, bool noIntegrityCheck)
    {
        if (!AdministratorChecker.IsAdministrator())
            throw new SrmOperationException(
                "srm audit には管理者権限が必要です。管理者として実行したシェルから再度実行してください。", 3);

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

        // design.md Open Question 3（Tier1限定にするか）はTier1限定で確定させた:
        // Tier2をサポートするにはStackTraceOperation/DC-021と同様、ゲスト内
        // `--nested`自身にETWセッションを張らせてcontrol/経由で結果を転送する
        // 追加の配線が必要で、v1のスコープを大きく超えるため。
        if (policy.Tier != 1)
            throw new SrmOperationException(
                "srm audit は現時点でTier1（AppContainer）ポリシーのみに対応しています。" +
                "Tier2は今後の拡張対象です（openspec/changes/2026-08-14-resource-access-audit-logging/design.md 参照）。", 1);

        if (scopePaths.Count == 0)
            throw new SrmOperationException(
                "監視対象パスが指定されていません。--scope <path> を1つ以上指定するか、" +
                "application.working_directory が設定されたポリシーを使用してください。", 1);

        var logger = new StructuredLogger(policy.Name, policy.Logging.RetentionDays);
        logger.Info("srm audit 開始", new { policy = policyName, tier = policy.Tier, scope_paths = scopePaths });

        // srm runとは別のAppContainerプロファイル名にする: 同じポリシー名で
        // `srm run`と`srm audit`を同時/交互に使っても、ACL付与やプロファイルの
        // ライフサイクルが互いに干渉しないようにするため。
        using var sidFactory = AppContainerSidFactory.Create($"srm-audit-{policy.Name}");
        var aclMgr = new AclManager();
        aclMgr.GrantReadAccessForAudit(scopePaths, sidFactory.Sid);
        aclMgr.GrantExecuteAccess(policy.Application.Executable, sidFactory.Sid);
        logger.Info("ACL設定完了（audit、読み取りのみ）", new { scope_paths = scopePaths, executable = policy.Application.Executable });

        // WindowGuard等が別プロセスから再オープンする用途はauditには無いが、
        // srm runと同じ命名規約に揃えておく（`srm-job-audit-<policy>`）。
        var jobName = $"srm-job-audit-{policy.Name}";
        using var job = JobObjectManager.Create(policy.Process.MaxProcesses, jobName);

        // forceInternetCapability: true — allow_hostsの内容に関わらず
        // AppContainerのinternetClientケーパビリティを付与する。WFPフィルターを
        // 一切登録しないため、これが無いとWindows組み込みのAppContainerネットワーク
        // 隔離により全アウトバウンド通信がブロックされ、観測が意味を成さなくなる。
        var launcher = new AppContainerLauncher();
        var result = launcher.Launch(policy, sidFactory.Sid, forceInternetCapability: true);
        logger.Info("プロセス起動（audit）", new { pid = result.ProcessId, exe = policy.Application.Executable });

        try { job.AssignProcess(result.ProcessHandle); }
        catch (System.ComponentModel.Win32Exception) { /* ベストエフォート（srm runと同じ扱い） */ }

        return new AuditOperationResult
        {
            Policy = policy,
            Pid = result.ProcessId,
            JobName = jobName,
            Logger = logger,
        };
    }
}

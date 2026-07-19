using Srm.PolicyEngine;
using Srm.PolicyIntegrity;

namespace Srm.Runtime.Operations;

public class ValidateOperationResult
{
    public required string PolicyName { get; init; }
    public required int Tier { get; init; }
    public required string Executable { get; init; }
    public string? SidecarPath { get; init; }
    public List<string> Warnings { get; init; } = new();
}

// srm validate のロジック本体。元はSrm.Cli.Commands.ValidateCommandに直書きされて
// いたものをここに移した（RunOperation/StopOperationと同じ理由）。
public class ValidateOperation
{
    public ValidateOperationResult Execute(string policiesDir, string policyName, bool sign)
    {
        var path = ResolvePolicyPath(policiesDir, policyName);
        string? sidecarPath = null;

        if (sign)
        {
            sidecarPath = new IntegrityWriter().Write(path);
        }
        else
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

        var result = new PolicyValidator().Validate(policy);
        if (!result.IsValid)
        {
            var detail = string.Join("\n", result.Errors.Select(e => $"  - {e}"));
            throw new SrmOperationException($"エラー: {policyName} のバリデーションに失敗しました\n{detail}", 1);
        }

        var warnings = new List<string>();

        // tier2-guest-network-egress: DC-010は既定でネスト先をAppContainerでラップするが、
        // このopt-outを選んだポリシーはfs/プロセスの隔離保証がAppContainer相当から
        // VM境界のみに後退する（restricted-account-app-isolation実装前は特に）。
        // これはpolicy.yaml側の意図的な選択なので実行自体は妨げないが、validateの
        // 時点で利用者に明示しておく（proposal.mdのBREAKING注記・design.mdのRisks参照）。
        if (policy.Tier == 2 && !policy.Tier2.AppContainer)
        {
            warnings.Add(
                "tier2.app_container: false が指定されています。AppContainerによる" +
                "fs/プロセスの隔離は適用されず、Windows Sandbox（VM境界）と" +
                "ネットワーク遮断のみが有効です。DC-016と互換性のないアプリ" +
                "（AppContainerトークン下で名前付きパイプ生成が失敗するアプリ等）向けの" +
                "意図的な選択でない場合は見直してください。");
        }

        return new ValidateOperationResult
        {
            PolicyName = policyName,
            Tier = policy.Tier,
            Executable = policy.Application.Executable,
            SidecarPath = sidecarPath,
            Warnings = warnings,
        };
    }

    private static string ResolvePolicyPath(string policiesDir, string name)
    {
        var path = PolicyPathResolver.Resolve(policiesDir, name);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"ポリシーファイルが見つかりません: {path}\n" +
                $"次を確認してください: '{policiesDir}' ディレクトリに {name}.yaml があるか");
        return path;
    }
}

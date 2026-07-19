namespace Srm.Runtime.Operations;

public class CleanupAccountOperationResult
{
    public required string PolicyName { get; init; }
}

// restricted-account-app-isolation: design.mdのライフサイクル決定 — 専用アカウントは
// `srm stop`では削除しない（AppContainerプロファイルを実行間で使い回す既存方針と
// 揃える、tasks.md 2.4）。ポリシーの利用をやめる際にアカウントを片付けるための、
// RunOperation/StopOperationと同じ「Console出力・Environment.Exitを行わない」形の
// 独立した操作。「srm policy remove」のようなポリシー管理コマンド自体はこの
// プロジェクトにまだ存在しない（ポリシーはYAMLファイルそのものが実体）ため、
// アカウント削除だけに絞った狭いコマンドとして提供する。
public class CleanupAccountOperation
{
    public CleanupAccountOperationResult Execute(string policyName)
    {
        if (!AdministratorChecker.IsAdministrator())
            throw new SrmOperationException(
                "srm cleanup-account には管理者権限が必要です。管理者として実行したシェルから再度実行してください。", 3);

        if (string.IsNullOrWhiteSpace(policyName))
            throw new SrmOperationException("ポリシー名を指定してください。", 1);

        RestrictedAccountManager.DeleteAccount(policyName);

        return new CleanupAccountOperationResult { PolicyName = policyName };
    }
}

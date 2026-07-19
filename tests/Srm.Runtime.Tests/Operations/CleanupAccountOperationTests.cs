using Srm.Runtime.Operations;
using Xunit;

namespace Srm.Runtime.Tests.Operations;

// restricted-account-app-isolation: tasks.md 2.4のPoC。専用アカウントは`srm stop`では
// 削除されない（design.mdのライフサイクル決定）ため、独立した`cleanup-account`操作が
// 実際にローカルアカウントを削除することを実機で確認する。管理者権限が必要なため、
// RestrictedAccountTests等と同じくWindows実機でのみ実行される。
public class CleanupAccountOperationTests
{
    [WindowsOnlyFact]
    public void Execute_DeletesAccountCreatedByRestrictedAccountManager()
    {
        var policyName = $"srm-test-cleanup-{Guid.NewGuid():N}";
        var account = RestrictedAccountManager.EnsureAccount(policyName);

        var result = new CleanupAccountOperation().Execute(policyName);

        Assert.Equal(policyName, result.PolicyName);
        Assert.Throws<System.ComponentModel.Win32Exception>(() =>
        {
            // アカウントが本当に削除されていれば、同名でのログオン試行は
            // ERROR_LOGON_FAILURE系で失敗する（EnsureAccountが返したパスワードは
            // もう有効なアカウントに紐づいていない）。
            if (!Srm.Runtime.Native.AccountNative.LogonUserW(
                    account.Username, ".", account.Password,
                    Srm.Runtime.Native.AccountNative.LOGON32_LOGON_INTERACTIVE,
                    Srm.Runtime.Native.AccountNative.LOGON32_PROVIDER_DEFAULT,
                    out _))
                throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        });
    }

    [WindowsOnlyFact]
    public void Execute_NonExistentAccount_DoesNotThrow()
    {
        var policyName = $"srm-test-cleanup-never-existed-{Guid.NewGuid():N}";

        var result = new CleanupAccountOperation().Execute(policyName);

        Assert.Equal(policyName, result.PolicyName);
    }
}

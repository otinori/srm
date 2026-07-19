using System.Security.AccessControl;
using System.Security.Principal;
using Srm.PolicyEngine.Models;
using Xunit;

namespace Srm.Runtime.Tests;

// restricted-account-app-isolation: tasks.md 3.3のPoC。design.mdの二段構え
// （祖先Traverseは共有SRM-RestrictedApps SIDへ、allow_pathsへの実アクセスは
// ポリシーごとの専用アカウントSIDへ）が実機のACL上で実際にその形になっていること、
// および一方のポリシーの専用アカウントがもう一方のポリシーのallow_pathsへ直接
// アクセスできないことを、DirectorySecurity経由で確認する。3.2の手動確認
// （Get-Acl目視）を自動テスト化したもの。
public class AclManagerRestrictedAccountTests
{
    [WindowsOnlyFact]
    public void GrantAccess_AllowPathGoesToAccountSid_AncestorTraverseGoesToSharedGroupSidOnly()
    {
        var policyName = $"srm-test-aclmgr-{Guid.NewGuid():N}";
        var workDir = Path.Combine(Path.GetTempPath(), $"srm-aclmgr-test-{Guid.NewGuid():N}");
        var allowedDir = Path.Combine(workDir, "allowed");

        var account = RestrictedAccountManager.EnsureAccount(policyName);
        try
        {
            var sharedGroupSid = new SecurityIdentifier(account.SharedGroupSid);
            var accountSid = new SecurityIdentifier(account.Sid);

            new AclManager().GrantAccess(
                new List<AllowedPath> { new() { Path = allowedDir, Access = "rw" } },
                account.Sid, account.SharedGroupSid, sharedGroupSid);

            // allow_pathsフォルダ自体には専用アカウントSIDへの直接ACEが付与されている必要がある。
            var allowedAcl = new DirectoryInfo(allowedDir).GetAccessControl(AccessControlSections.Access);
            var accountHasDirectAccess = allowedAcl.GetAccessRules(true, false, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .Any(r => r.AccessControlType == AccessControlType.Allow && ((SecurityIdentifier)r.IdentityReference).Equals(accountSid));
            Assert.True(accountHasDirectAccess, "allow_pathsフォルダには専用アカウントSIDへの直接ACEが必要");

            // 祖先ディレクトリ(workDir)のTraverse権限は共有グループSIDへ付与され、
            // 専用アカウントSID個別のACEは付与されない（design.mdの二段構え）。
            var ancestorAcl = new DirectoryInfo(workDir).GetAccessControl(AccessControlSections.Access);
            var explicitRules = ancestorAcl.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();

            var sharedGroupHasTraverse = explicitRules.Any(r =>
                r.AccessControlType == AccessControlType.Allow && ((SecurityIdentifier)r.IdentityReference).Equals(sharedGroupSid));
            Assert.True(sharedGroupHasTraverse, "祖先ディレクトリのTraverse権限は共有グループSIDへ付与される必要がある");

            var accountHasDirectAncestorAccess = explicitRules.Any(r => ((SecurityIdentifier)r.IdentityReference).Equals(accountSid));
            Assert.False(accountHasDirectAncestorAccess, "祖先ディレクトリに専用アカウントSID個別のACEを付与すべきではない（共有グループのみでよい）");
        }
        finally
        {
            RestrictedAccountManager.DeleteAccount(policyName);
            try { Directory.Delete(workDir, recursive: true); } catch { /* ベストエフォート */ }
        }
    }

    [WindowsOnlyFact]
    public void GrantAccess_SecondPolicysAccount_HasNoDirectAccessToFirstPolicysAllowPath()
    {
        var policyA = $"srm-test-aclmgr-a-{Guid.NewGuid():N}";
        var policyB = $"srm-test-aclmgr-b-{Guid.NewGuid():N}";
        var workDir = Path.Combine(Path.GetTempPath(), $"srm-aclmgr-isolation-{Guid.NewGuid():N}");
        var allowedDirA = Path.Combine(workDir, "allowed-a");

        var accountA = RestrictedAccountManager.EnsureAccount(policyA);
        var accountB = RestrictedAccountManager.EnsureAccount(policyB);
        try
        {
            var sharedGroupSid = new SecurityIdentifier(accountA.SharedGroupSid);

            new AclManager().GrantAccess(
                new List<AllowedPath> { new() { Path = allowedDirA, Access = "rw" } },
                accountA.Sid, accountA.SharedGroupSid, sharedGroupSid);

            var allowedAcl = new DirectoryInfo(allowedDirA).GetAccessControl(AccessControlSections.Access);
            var accountBSid = new SecurityIdentifier(accountB.Sid);
            var accountBHasAccess = allowedAcl.GetAccessRules(true, false, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .Any(r => r.AccessControlType == AccessControlType.Allow && ((SecurityIdentifier)r.IdentityReference).Equals(accountBSid));

            Assert.False(accountBHasAccess, "policy Bの専用アカウントはpolicy Aのallow_pathsへ直接アクセスできてはならない");
        }
        finally
        {
            RestrictedAccountManager.DeleteAccount(policyA);
            RestrictedAccountManager.DeleteAccount(policyB);
            try { Directory.Delete(workDir, recursive: true); } catch { /* ベストエフォート */ }
        }
    }
}

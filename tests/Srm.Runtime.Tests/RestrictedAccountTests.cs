using System.Diagnostics;
using Srm.PolicyEngine.Models;
using Xunit;

namespace Srm.Runtime.Tests;

// restricted-account-app-isolation: tasks.md 1.1のPoC。ローカルアカウント・共有グループの
// 払い出し（RestrictedAccountManager）が実機で動作することを確認する。管理者権限が必要な
// ため、JobObjectManagerTests等と同じくWindows実機でのみ実行される。
public class RestrictedAccountTests
{
    [WindowsOnlyFact]
    public void EnsureAccount_IsIdempotentAndReturnsSid()
    {
        var policyName = $"srm-test-account-{Guid.NewGuid():N}";
        try
        {
            var first = RestrictedAccountManager.EnsureAccount(policyName);
            var second = RestrictedAccountManager.EnsureAccount(policyName);

            Assert.Equal(first.Username, second.Username);
            Assert.NotEqual(first.Password, second.Password); // 毎回リセットされる
            Assert.NotEqual(IntPtr.Zero, first.Sid);
        }
        finally
        {
            RestrictedAccountManager.DeleteAccount(policyName);
        }
    }

    // tasks.md 1.1〜1.3のPoC（Low ILピボット後）: DC-023でCreateRestrictedTokenの
    // 組み合わせを断念した後の新方式（素のログオントークンをLow整合性レベルへ
    // 引き下げてCreateProcessWithTokenWで起動）が、実機で書き込みバリアとして
    // 実際に機能することを確認する。allow_pathsに相当するLow ILラベル付きフォルダへの
    // 書き込みは成功し、ラベルなし（既定Medium IL）フォルダへの書き込みはOSのMICにより
    // 拒否される、という非対称性がこのメカニズムの中核的な保証。
    [WindowsOnlyFact]
    public void Launch_LowIntegrityProcess_CanWriteLabeledPath_CannotWriteUnlabeledPath()
    {
        var policyName = $"srm-test-lowil-{Guid.NewGuid():N}";
        var workDir = Path.Combine(Path.GetTempPath(), $"srm-lowil-test-{Guid.NewGuid():N}");
        var allowedDir = Path.Combine(workDir, "allowed");
        var deniedDir = Path.Combine(workDir, "denied");
        Directory.CreateDirectory(allowedDir);
        Directory.CreateDirectory(deniedDir);

        // TrustedInstaller保護下のSystem32\cmd.exeを直接対象にすると、
        // 保護ファイル自体のACLへは新規ACEが反映されない（DC-023の教訓）ため、
        // 非保護の作業フォルダへコピーしたものを対象にする。
        var cmdCopy = Path.Combine(workDir, "cmd.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), cmdCopy);

        var account = RestrictedAccountManager.EnsureAccount(policyName);
        var aclManager = new AclManager();

        try
        {
            var sharedGroupSid = new System.Security.Principal.SecurityIdentifier(account.SharedGroupSid);

            aclManager.GrantExecuteAccess(cmdCopy, account.Sid, account.SharedGroupSid, sharedGroupSid);
            aclManager.GrantAccess(
                new List<AllowedPath> { new() { Path = allowedDir, Access = "rw" } },
                account.Sid, account.SharedGroupSid, sharedGroupSid);
            aclManager.SetLowIntegrityLabel(allowedDir);
            // deniedDirは意図的にLowラベルを付与しない（既定Medium ILのまま）。
            // 通常アカウントのトークンはBUILTIN\Usersを継承するため、作業フォルダ配下の
            // 新規ディレクトリには既定でこのアカウントの書き込み権があり、ACL自体は
            // 拒否要因にならない — 拒否要因はLow ILの書き込みバリアのみになる。

            var policy = new PolicyModel
            {
                Name = policyName,
                Application = new ApplicationConfig
                {
                    Executable = cmdCopy,
                    Arguments =
                        $"/c echo allowed_ok> \"{Path.Combine(allowedDir, "out.txt")}\" " +
                        $"& echo denied_attempt> \"{Path.Combine(deniedDir, "out.txt")}\" " +
                        $"& echo done> \"{Path.Combine(allowedDir, "done.txt")}\"",
                    WorkingDirectory = workDir,
                },
            };

            var launcher = new RestrictedAccountLauncher();
            var result = launcher.Launch(policy, account);

            using var process = Process.GetProcessById(result.ProcessId);
            var exited = process.WaitForExit(15000);
            Assert.True(exited, "Low ILプロセスが15秒以内に終了しませんでした");

            Assert.True(File.Exists(Path.Combine(allowedDir, "done.txt")),
                "コマンド列全体が完走した形跡がありません（done.txtが存在しない）");
            Assert.True(File.Exists(Path.Combine(allowedDir, "out.txt")),
                "Low ILラベル付きフォルダへの書き込みが失敗しました");
            Assert.False(File.Exists(Path.Combine(deniedDir, "out.txt")),
                "Low ILラベルなし（Medium IL既定）フォルダへの書き込みが成功してしまいました（書き込みバリアが機能していません）");
        }
        finally
        {
            RestrictedAccountManager.DeleteAccount(policyName);
            try { Directory.Delete(workDir, recursive: true); } catch { /* ベストエフォート */ }
        }
    }
}

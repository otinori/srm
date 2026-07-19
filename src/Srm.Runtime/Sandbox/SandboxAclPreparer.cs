using System.ComponentModel;
using Srm.PolicyEngine.Models;
using Srm.Runtime.Native;

namespace Srm.Runtime.Sandbox;

// VM起動前にホスト側でAppContainer SIDを事前計算し、マップフォルダ元のホストパスへ
// ACLを事前付与する（DC-010）。DeriveAppContainerSidFromAppContainerNameは名前の
// SHA256から決定的にSIDを算出する純粋関数で、CreateAppContainerProfileのような
// プロファイル登録を伴わない。プロファイル登録自体は%LOCALAPPDATA%\Packages\への
// マシンローカルな登録が必要なため、ゲスト内でAppContainerLauncherが行う
// （ここで計算するSIDの値そのものはゲスト内で計算されるものと一致する）。
public class SandboxAclPreparer
{
    private readonly AclManager _aclManager = new();

    public void Prepare(PolicyModel policy, SandboxRunPaths paths)
    {
        var sid = DeriveSid($"srm-{policy.Name}");
        try
        {
            _aclManager.GrantAccess(new List<AllowedPath>
            {
                new() { Path = paths.Outbox, Access = "rw" },
                new() { Path = paths.Input, Access = "r" },
                new() { Path = paths.Control, Access = "rw" },
            }, sid);
        }
        finally
        {
            AppContainerNative.FreeSid(sid);
        }
    }

    private static IntPtr DeriveSid(string appContainerName)
    {
        int hr = AppContainerNative.DeriveAppContainerSidFromAppContainerName(appContainerName, out var sid);
        if (hr != 0 || sid == IntPtr.Zero)
            throw new Win32Exception(hr, $"AppContainer SIDの導出に失敗しました: {appContainerName}");
        return sid;
    }
}

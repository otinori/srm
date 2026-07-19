using System.Security.Principal;

namespace Srm.Runtime;

public static class AdministratorChecker
{
    /// <summary>
    /// AppContainer/ACL/WFP 操作は管理者権限が必須。特にWFPエンジンへの接続
    /// (FwpmEngineOpen0) は権限不足時にエラーを返さずRPCハンドシェイクで
    /// 無応答のままハングすることがあるため、これらの操作に入る前に必ず
    /// 呼び出すこと（ハング後に検出する手段がないため事前チェックが必須）。
    /// </summary>
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}

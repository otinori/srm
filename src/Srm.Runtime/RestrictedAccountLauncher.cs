using System.ComponentModel;
using System.Runtime.InteropServices;
using Srm.PolicyEngine.Models;
using Srm.Runtime.Native;

namespace Srm.Runtime;

// restricted-account-app-isolation: design.mdのLow Integrity Levelピボット決定を実装する。
// AppContainerLauncherと並行する起動経路であり、AppContainer機構は一切経由しない
// （DC-016のAppContainerトークン固有の失敗モードを構造的に踏まない）。
//
// 【設計の転換（DC-023）】当初はCreateRestrictedTokenでBUILTIN\Users・Authenticated
// Users・Everyoneを無効化する方式を実装したが、実機検証でCreateProcessAsUserW・
// ImpersonateLoggedOnUser+CreateProcess・CreateProcessWithTokenWの3つの標準起動APIの
// いずれとも相性が悪く、それぞれ別の失敗モード（特にCreateProcessWithTokenWは
// Everyone無効化時にERROR_BLOCKED_BY_PARENTAL_CONTROLS(346)）を示したため断念した。
// 代わりに、アカウントの素のログオントークン（CreateRestrictedTokenを一切経由しない）
// をそのまま使い、SetTokenInformation(TokenIntegrityLevel)でLow整合性レベルへ
// 引き下げてからCreateProcessWithTokenWで起動する。Windows MIC（Mandatory Integrity
// Control）が、Low ILプロセスがMedium IL以上のオブジェクトへ書き込むことを構造的に
// 拒否する（DACLの許可内容に関わらず）。この書き込みバリアはallow_pathsフォルダの
// マンダトリラベルをLowへ明示的に引き下げること（AclManager.SetLowIntegrityLabel）と
// 組み合わせて初めて機能する。読み取りバリアではない点はdesign.mdのRisksに明記済み。
public class RestrictedAccountLauncher
{
    public LaunchResult Launch(
        PolicyModel policy, RestrictedAccount account,
        IReadOnlyDictionary<string, string>? extraEnvironmentVariables = null)
    {
        var exe = policy.Application.Executable;
        var args = policy.Application.Arguments;
        var wd = policy.Application.WorkingDirectory;

        var commandLine = string.IsNullOrWhiteSpace(args)
            ? $"\"{exe}\""
            : $"\"{exe}\" {args}";

        var primaryToken = IntPtr.Zero;
        var environmentBlockPtr = IntPtr.Zero;

        try
        {
            // LOGON32_LOGON_BATCHはサービス的な非対話ログオンを意図しているが、
            // 対象アカウントに「バッチジョブとしてログオン」権限が付与されていない場合
            // ERROR_LOGON_TYPE_NOT_GRANTED(1385)で失敗しうる。失敗した場合は
            // LOGON32_LOGON_INTERACTIVEにフォールバックする（通常アカウントは
            // 既定でこの種別のログオンが許可されている）。
            if (!AccountNative.LogonUserW(
                    account.Username, ".", account.Password,
                    AccountNative.LOGON32_LOGON_BATCH, AccountNative.LOGON32_PROVIDER_DEFAULT,
                    out primaryToken))
            {
                if (!AccountNative.LogonUserW(
                        account.Username, ".", account.Password,
                        AccountNative.LOGON32_LOGON_INTERACTIVE, AccountNative.LOGON32_PROVIDER_DEFAULT,
                        out primaryToken))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        $"アカウント{account.Username}のログオンに失敗しました");
            }

            LowerTokenIntegrityLevel(primaryToken);
            GrantWindowStationAndDesktopAccess(account.Sid);

            // SeAssignPrimaryTokenPrivilegeではなくSeImpersonatePrivilegeのみを要求する
            // CreateProcessWithTokenWを使う（DC-023で「零件無効化トークン」に対しては
            // これが機能することを確認済み。CreateRestrictedTokenを経由しない今回の
            // トークンはまさにその「零件無効化」相当の形）。
            EnablePrivilege(AccountNative.SE_IMPERSONATE_NAME);

            // 【実機で判明した重要な結果】別ユーザーのトークンでCreateProcessWithTokenWを
            // 呼ぶと、フラグを何も指定しなくても新規コンソール確保が発生し、
            // conhost/csrssとのALPC交渉がハングする（スレッドがLpcReply待ちのまま
            // 応答が返らない）。DETACHED_PROCESS/CREATE_NO_WINDOWで回避を試みたが、
            // CreateProcessWithTokenWがドキュメント上受け付けるdwCreationFlagsには
            // どちらも含まれておらずERROR_INVALID_PARAMETER(87)で拒否される。
            // 根本原因はLOGON32_LOGON_BATCH/INTERACTIVEで取得したトークンが対話的
            // ウィンドウステーション（WinSta0）に既定でバインドされないことだった。
            // STARTUPINFO.lpDesktopを明示的に"WinSta0\Default"に設定することで
            // conhostが正しいウィンドウステーションで新規コンソールを確保できるように
            // なり、ハングが解消することを実機で確認した。
            var startupInfo = new AppContainerNative.STARTUPINFO
            {
                cb = Marshal.SizeOf<AppContainerNative.STARTUPINFO>(),
                lpDesktop = @"WinSta0\Default",
            };

            var creationFlags = AppContainerNative.CREATE_NEW_CONSOLE;
            if (extraEnvironmentVariables is { Count: > 0 })
            {
                environmentBlockPtr = BuildAccountEnvironmentBlock(primaryToken, extraEnvironmentVariables);
                creationFlags |= AppContainerNative.CREATE_UNICODE_ENVIRONMENT;
            }

            if (!AccountNative.CreateProcessWithTokenW(
                    primaryToken,
                    0,
                    null,
                    commandLine,
                    creationFlags,
                    environmentBlockPtr,
                    string.IsNullOrWhiteSpace(wd) ? null : wd,
                    ref startupInfo,
                    out var pi))
            {
                var err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err,
                    $"プロセスの起動に失敗しました: {exe} (Win32エラーコード: {err})\n" +
                    "次を確認してください: 実行ファイルのパスが正しいか、対象アカウントからこのパスへの実行権があるか");
            }

            return new LaunchResult
            {
                ProcessId = pi.dwProcessId,
                ProcessHandle = pi.hProcess,
                ThreadHandle = pi.hThread,
            };
        }
        finally
        {
            if (environmentBlockPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(environmentBlockPtr);
            if (primaryToken != IntPtr.Zero)
                AppContainerNative.CloseHandle(primaryToken);
        }
    }

    // 対象トークン自身のプロファイルから正しい土台（USERPROFILE/TEMP等）を取得した上で
    // extraをマージする。EnvironmentBlockBuilder.Build(呼び出し元自身の環境が土台)を
    // そのまま流用すると、対象アカウントのプロセスに呼び出し元(Administrator)の
    // 環境変数が漏れてしまうため、ここだけは専用の土台取得を行う。
    private static IntPtr BuildAccountEnvironmentBlock(IntPtr token, IReadOnlyDictionary<string, string> extra)
    {
        if (!AccountNative.CreateEnvironmentBlock(out var tokenEnvPtr, token, false))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "対象アカウントの環境ブロック取得に失敗しました");

        try
        {
            var baseEnv = EnvironmentBlockBuilder.Parse(tokenEnvPtr);
            return EnvironmentBlockBuilder.Build(baseEnv, extra);
        }
        finally
        {
            AccountNative.DestroyEnvironmentBlock(tokenEnvPtr);
        }
    }

    private static void LowerTokenIntegrityLevel(IntPtr token)
    {
        var labelSid = IntPtr.Zero;
        var labelPtr = IntPtr.Zero;
        try
        {
            if (!AclNative.ConvertStringSidToSidW(AccountNative.LOW_INTEGRITY_SID, out labelSid) || labelSid == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Low整合性レベルSIDの変換に失敗しました");

            var label = new AccountNative.TOKEN_MANDATORY_LABEL
            {
                Label = new AppContainerNative.SID_AND_ATTRIBUTES { Sid = labelSid, Attributes = 0 },
            };

            labelPtr = Marshal.AllocHGlobal(Marshal.SizeOf<AccountNative.TOKEN_MANDATORY_LABEL>());
            Marshal.StructureToPtr(label, labelPtr, false);

            if (!AccountNative.SetTokenInformation(
                    token, AccountNative.TokenIntegrityLevel, labelPtr,
                    (uint)Marshal.SizeOf<AccountNative.TOKEN_MANDATORY_LABEL>()))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "トークンのLow整合性レベルへの設定に失敗しました");
        }
        finally
        {
            if (labelPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(labelPtr);
            if (labelSid != IntPtr.Zero)
                AclNative.LocalFree(labelSid);
        }
    }

    // restricted-account-app-isolation: 対象アカウントに対話的ウィンドウステーション(WinSta0)・
    // デスクトップ(Default)への明示的なDACLエントリを付与する。省略すると、コンソール
    // アプリの起動がconhost.exe生成の手前で無期限にハングする（実機確認済み、上記コメント
    // 参照）。毎回の呼び出しでACEを追記する単純な実装（重複ACEが蓄積しうる）で、
    // 冪等化は将来の改善課題としてtasks.mdに記録する。
    private static void GrantWindowStationAndDesktopAccess(IntPtr sid)
    {
        var hWinSta = UserObjectNative.OpenWindowStationW("WinSta0", false, UserObjectNative.READ_CONTROL | UserObjectNative.WRITE_DAC);
        if (hWinSta == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WinSta0のオープンに失敗しました");
        try
        {
            GrantUserObjectAccess(hWinSta, sid, UserObjectNative.WINSTA_ALL_ACCESS);
        }
        finally
        {
            UserObjectNative.CloseWindowStation(hWinSta);
        }

        var hDesktop = UserObjectNative.OpenDesktopW("Default", 0, false, UserObjectNative.READ_CONTROL | UserObjectNative.WRITE_DAC);
        if (hDesktop == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Defaultデスクトップのオープンに失敗しました");
        try
        {
            GrantUserObjectAccess(hDesktop, sid, UserObjectNative.DESKTOP_ALL_ACCESS);
        }
        finally
        {
            UserObjectNative.CloseDesktop(hDesktop);
        }
    }

    private static void GrantUserObjectAccess(IntPtr hObj, IntPtr sid, uint accessMask)
    {
        var info = (uint)AclNative.SECURITY_INFORMATION.DACL_SECURITY_INFORMATION;
        UserObjectNative.GetUserObjectSecurity(hObj, ref info, IntPtr.Zero, 0, out var needed);

        var oldSdPtr = IntPtr.Zero;
        var newSdPtr = IntPtr.Zero;
        var newDacl = IntPtr.Zero;
        try
        {
            if (needed == 0)
                throw new InvalidOperationException("GetUserObjectSecurityのサイズ取得が0を返しました");
            oldSdPtr = Marshal.AllocHGlobal((int)needed);
            if (!UserObjectNative.GetUserObjectSecurity(hObj, ref info, oldSdPtr, needed, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "ユーザーオブジェクトのセキュリティ記述子取得に失敗しました");

            if (!UserObjectNative.GetSecurityDescriptorDacl(oldSdPtr, out _, out var oldDacl, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "既存DACLの取得に失敗しました");

            var explicitAccess = new AclNative.EXPLICIT_ACCESS
            {
                grfAccessPermissions = accessMask,
                grfAccessMode = AclNative.GRANT_ACCESS,
                grfInheritance = AclNative.NO_INHERITANCE,
                Trustee = new AclNative.TRUSTEE
                {
                    pMultipleTrustee = IntPtr.Zero,
                    MultipleTrusteeOperation = 0,
                    TrusteeForm = AclNative.TRUSTEE_FORM.TRUSTEE_IS_SID,
                    TrusteeType = AclNative.TRUSTEE_TYPE.TRUSTEE_IS_USER,
                    ptstrName = sid,
                },
            };

            var ret = AclNative.SetEntriesInAcl(1, new[] { explicitAccess }, oldDacl, out newDacl);
            if (ret != AclNative.ERROR_SUCCESS)
                throw new Win32Exception((int)ret, "新しいDACLの生成に失敗しました");

            newSdPtr = Marshal.AllocHGlobal(UserObjectNative.SECURITY_DESCRIPTOR_MIN_LENGTH);
            if (!UserObjectNative.InitializeSecurityDescriptor(newSdPtr, UserObjectNative.SECURITY_DESCRIPTOR_REVISION))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "セキュリティ記述子の初期化に失敗しました");

            if (!UserObjectNative.SetSecurityDescriptorDacl(newSdPtr, true, newDacl, false))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "新しいDACLの設定に失敗しました");

            if (!UserObjectNative.SetUserObjectSecurity(hObj, ref info, newSdPtr))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "ユーザーオブジェクトへのセキュリティ記述子設定に失敗しました");
        }
        finally
        {
            if (newDacl != IntPtr.Zero)
                AclNative.LocalFree(newDacl);
            if (newSdPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(newSdPtr);
            if (oldSdPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(oldSdPtr);
        }
    }

    private static void EnablePrivilege(string name)
    {
        if (!AccountNative.OpenProcessToken(
                System.Diagnostics.Process.GetCurrentProcess().Handle,
                AccountNative.TOKEN_QUERY | AccountNative.TOKEN_ADJUST_PRIVILEGES,
                out var hToken))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "プロセストークンのオープンに失敗しました");

        try
        {
            if (!AccountNative.LookupPrivilegeValueW(null, name, out var luid))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"特権の解決に失敗しました: {name}");

            var tp = new AccountNative.TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new AccountNative.LUID_AND_ATTRIBUTES
                {
                    Luid = luid,
                    Attributes = AccountNative.SE_PRIVILEGE_ENABLED,
                },
            };

            if (!AccountNative.AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"特権の有効化に失敗しました: {name}");

            // AdjustTokenPrivilegesは対象特権を1件も保持していない場合でもtrueを返し、
            // GetLastError=ERROR_NOT_ALL_ASSIGNED(1300)でのみそれを示す。
            const int ErrorNotAllAssigned = 1300;
            if (Marshal.GetLastWin32Error() == ErrorNotAllAssigned)
                throw new InvalidOperationException(
                    $"特権{name}を保持していません。管理者権限で実行しているか確認してください。");
        }
        finally
        {
            AppContainerNative.CloseHandle(hToken);
        }
    }
}

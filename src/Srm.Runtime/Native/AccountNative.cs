using System.Runtime.InteropServices;

namespace Srm.Runtime.Native;

// restricted-account-app-isolation: ローカルアカウント作成・制限付きトークンでのプロセス
// 起動に使うP/Invoke宣言。既存コード（AclNative/AppContainerNative）と同じく、.NET高レベル
// ラッパー（System.DirectoryServices.AccountManagement等）は使わず生のWin32 APIを直接
// 呼ぶ方針を踏襲する（DC-018で顕在化した共有publishフォルダでの依存バージョン衝突リスクを
// 新規パッケージ追加で再導入しないため）。
internal static class AccountNative
{
    // NetUserAdd/NetUserSetInfo/NetUserDel/NetUserGetInfoはnetapi32.dllのローカルアカウント
    // 管理API。servernameにnullを渡すとローカルマシンが対象になる。
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint NetUserAdd(string? servername, uint level, ref USER_INFO_1 buf, out uint parm_err);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint NetUserSetInfo(string? servername, string username, uint level, ref USER_INFO_1 buf, out uint parm_err);

    // レベル1003は「パスワードのみ変更」専用（USER_INFO_1003、フィールド1つだけ）。
    // レベル1のUSER_INFO_1でパスワードだけ変更しようとすると、priv/flags等の他フィールドも
    // まとめて再検証され原因不明のERROR_INVALID_PARAMETER(87)を返すことが実機で判明した
    // ため、既存アカウントのパスワードリセットにはこちらを使う。
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint NetUserSetInfo(string? servername, string username, uint level, ref USER_INFO_1003 buf, out uint parm_err);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint NetUserDel(string? servername, string username);

    // level=0のUSER_INFO_0（名前のみ）で存在確認だけ行う。存在しない場合は
    // NERR_UserNotFound(2221)を返す。
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint NetUserGetInfo(string? servername, string username, uint level, out IntPtr bufptr);

    [DllImport("netapi32.dll")]
    internal static extern uint NetApiBufferFree(IntPtr buffer);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint NetLocalGroupAdd(string? servername, uint level, ref LOCALGROUP_INFO_1 buf, out uint parm_err);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint NetLocalGroupAddMembers(string? servername, string groupname, uint level, ref LOCALGROUP_MEMBERS_INFO_3 buf, uint totalentries);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool LookupAccountNameW(
        string? lpSystemName,
        string lpAccountName,
        IntPtr Sid,
        ref int cbSid,
        System.Text.StringBuilder ReferencedDomainName,
        ref int cchReferencedDomainName,
        out int peUse);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool LogonUserW(
        string lpszUsername,
        string? lpszDomain,
        string lpszPassword,
        uint dwLogonType,
        uint dwLogonProvider,
        out IntPtr phToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateRestrictedToken(
        IntPtr ExistingTokenHandle,
        uint Flags,
        uint DisableSidCount,
        IntPtr SidsToDisable,
        uint DeletePrivilegeCount,
        IntPtr PrivilegesToDelete,
        uint RestrictedSidCount,
        IntPtr SidsToRestrict,
        out IntPtr NewTokenHandle);

    // 【実機で判明した重要な結果】プロセスを別ユーザーのトークンで起動する手段を
    // 2つ試して両方失敗した: (1) CreateProcessAsUserWはSeAssignPrimaryTokenPrivilegeを
    // 要求するが、このマシンのAdministratorトークンはこの特権を保持しておらず
    // （既定ではLOCAL SERVICE/NETWORK SERVICEにのみ割り当て）ERROR_NOT_ALL_ASSIGNED(1300)
    // で失敗した。(2) ImpersonateLoggedOnUser（なりすまし）+通常のCreateProcessは、
    // ファイルDACLが正しく付与されている状態（PowerShellのGet-Aclで確認済み）でも
    // ERROR_ACCESS_DENIED(5)で一貫して失敗した。
    // 最終的にCreateProcessWithTokenWを採用した。これはまさに本用途
    // （呼び出し元が用意したトークンでプロセスを起動する。SeAssignPrimaryTokenPrivilege
    // ではなくSeImpersonatePrivilegeのみを要求）のために設計されたAPI。
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcessWithTokenW(
        IntPtr hToken,
        uint dwLogonFlags,
        string? lpApplicationName,
        string lpCommandLine,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref AppContainerNative.STARTUPINFO lpStartupInfo,
        out AppContainerNative.PROCESS_INFORMATION lpProcessInformation);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    // restricted-account-app-isolation: RestrictedAccountLauncherがmcp.allow_servers用の
    // 環境変数を対象プロセスへ渡す際に使う。lpEnvironment=IntPtr.Zeroのままだと
    // CreateProcessWithTokenWは対象トークンのプロファイルから正しいUSERPROFILE/TEMP等を
    // 自動導出してくれるが、追加の環境変数を渡すには自前でブロックを構築する必要が
    // あり、その際の土台を「呼び出し元(Administrator)自身の環境」ではなく「対象トークン
    // 自身の環境」から取得するために使う（土台を誤ると対象アカウントのプロセスに
    // 呼び出し元のUSERPROFILE等が漏れる）。
    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, [MarshalAs(UnmanagedType.Bool)] bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    // restricted-account-app-isolation: CreateRestrictedToken+CreateProcessWithTokenWの
    // 組み合わせが実機で複数の異なる失敗モードを示した（DC-023）ため、Low Integrity Level
    // （書き込みバリアのみ・成熟したWindows機能）へ設計を転換した。トークンの整合性レベルを
    // 下げるにはSetTokenInformation(TokenIntegrityLevel)を使う。
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetTokenInformation(
        IntPtr TokenHandle, int TokenInformationClass, IntPtr TokenInformation, uint TokenInformationLength);

    internal const int TokenIntegrityLevel = 25;
    // Mandatory Label\Low Mandatory Level のwell-known SID。
    internal const string LOW_INTEGRITY_SID = "S-1-16-4096";

    [StructLayout(LayoutKind.Sequential)]
    internal struct TOKEN_MANDATORY_LABEL
    {
        public AppContainerNative.SID_AND_ATTRIBUTES Label;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool LookupPrivilegeValueW(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AdjustTokenPrivileges(
        IntPtr TokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool DisableAllPrivileges,
        ref TOKEN_PRIVILEGES NewState,
        uint BufferLengthInBytes,
        IntPtr PreviousState,
        IntPtr ReturnLengthInBytes);

    internal const uint TOKEN_QUERY = 0x0008;
    internal const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    internal const uint SE_PRIVILEGE_ENABLED = 0x00000002;
    internal const string SE_IMPERSONATE_NAME = "SeImpersonatePrivilege";

    internal const uint NERR_Success = 0;
    internal const uint NERR_UserNotFound = 2221;
    internal const uint NERR_UserExists = 2224;
    internal const uint NERR_GroupExists = 2223;
    internal const uint ERROR_MEMBER_IN_ALIAS = 1378;
    // ローカルグループ（エイリアス）は「グループ名重複」をNERR_GroupExists(2223)ではなく
    // ERROR_ALIAS_EXISTS(1379)で返す（実機確認済み。NetLocalGroupAddの対象はSAMの
    // エイリアスオブジェクトであり、NERR_GroupExistsはグローバルグループ用のエラーコード）。
    internal const uint ERROR_ALIAS_EXISTS = 1379;

    internal const uint USER_PRIV_USER = 1;
    // UF_SCRIPT: NetUserAdd/SetInfoが要求する必須ビット。UF_NORMAL_ACCOUNT: 通常アカウント。
    // UF_DONT_EXPIRE_PASSWD: パスワード無期限（毎回リセットするため期限切れで
    // LogonUserが失敗する事態を避ける）。UF_PASSWORD_CANT_CHANGE: srm以外（アカウント
    // 自身含む）がパスワードを変更できないようにする（対話ログオンを想定しないため）。
    internal const uint UF_SCRIPT = 0x0001;
    internal const uint UF_PASSWORD_CANT_CHANGE = 0x0040;
    internal const uint UF_NORMAL_ACCOUNT = 0x0200;
    internal const uint UF_DONT_EXPIRE_PASSWD = 0x10000;

    // LogonUserのdwLogonType。BATCH(4)はサービス的な非対話用途向けで、対話ログオンの
    // セッション制約（インタラクティブウィンドウステーション等）を持ち込まない。
    // ローカルAdministrator（このsrmプロセス自身）から呼ぶ限り、対象アカウントに
    // 「バッチジョブとしてログオン」権限が付与されていなくても、通常はNT AUTHORITY\
    // Administratorsが暗黙に持つ権限で失敗しないことを実機で確認する（PoCの一部）。
    internal const uint LOGON32_LOGON_BATCH = 4;
    internal const uint LOGON32_LOGON_INTERACTIVE = 2;
    internal const uint LOGON32_PROVIDER_DEFAULT = 0;

    // DisableSidCountで渡すSIDのグループ属性を無効化する（Enabledフラグを落とす）方式。
    // Flagsは0（既定の動作）でよく、SidsToDisable配列のグループがトークンの
    // アクセスチェックで一致しなくなる。
    internal const uint DISABLE_MAX_PRIVILEGE = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct USER_INFO_1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string usri1_name;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri1_password;
        public uint usri1_password_age;
        public uint usri1_priv;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri1_home_dir;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri1_comment;
        public uint usri1_flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri1_script_path;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct USER_INFO_1003
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string usri1003_password;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct LOCALGROUP_INFO_1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string lgrpi1_name;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lgrpi1_comment;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct LOCALGROUP_MEMBERS_INFO_3
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string lgrmi3_domainandname;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privileges;
    }
}

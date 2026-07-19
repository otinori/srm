using System.Runtime.InteropServices;

namespace Srm.Runtime.Native;

internal static class AppContainerNative
{
    [DllImport("userenv.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int DeriveAppContainerSidFromAppContainerName(
        string pszAppContainerName,
        out IntPtr ppSidAppContainerSid);

    [DllImport("userenv.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int CreateAppContainerProfile(
        string pszAppContainerName,
        string pszDisplayName,
        string pszDescription,
        IntPtr pCapabilities,
        uint dwCapabilityCount,
        out IntPtr ppSidAppContainerSid);

    [DllImport("userenv.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int DeleteAppContainerProfile(string pszAppContainerName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList,
        int dwAttributeCount,
        int dwFlags,
        ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        uint dwFlags,
        IntPtr Attribute,
        IntPtr lpValue,
        IntPtr cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcess(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool FreeSid(IntPtr pSid);

    internal const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    internal const uint CREATE_NEW_CONSOLE = 0x00000010;
    // restricted-account-app-isolation: 【実機で判明】別ユーザーのトークンでCreateProcessWithTokenWを
    // 呼ぶと新規コンソール確保がconhost/csrssとのALPC交渉でハングすることがあったが、
    // 原因はコンソール確保そのものではなく、LOGON32_LOGON_BATCH/INTERACTIVEで取得した
    // トークンが対話的ウィンドウステーション（WinSta0）・デスクトップ（Default）への
    // アクセス権を持たないことだった（RestrictedAccountLauncher.
    // GrantWindowStationAndDesktopAccess参照）。なお、CreateProcessWithTokenWが
    // ドキュメント上受け付けるdwCreationFlagsにDETACHED_PROCESS/CREATE_NO_WINDOWは
    // 含まれておらず、それらを試すとERROR_INVALID_PARAMETER(87)で拒否される
    // （実機確認済み）。CREATE_NEW_CONSOLEは受け付けられるフラグの一覧に含まれる。
    // channel-d-guest-mcp-bridge: lpEnvironmentへ独自の環境ブロックを渡す際に必須。
    // 無いとCreateProcessはlpEnvironmentをANSI（システムロケール依存）として解釈する。
    internal const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    internal const int STARTF_USESTDHANDLES = 0x00000100;
    internal const IntPtr PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES = (IntPtr)0x00020009;

    // ProcThreadAttributeHandleList(=2) | PROC_THREAD_ATTRIBUTE_INPUT(=0x00020000)。
    // bInheritHandles=trueにした際に継承させるハンドルをここで明示したものだけに
    // 限定する（未指定だとプロセス内の継承可能な全ハンドル、たとえばMCPサーバー
    // 自身のJSON-RPC用stdioパイプまで子プロセスへ漏れうる）。
    internal const IntPtr PROC_THREAD_ATTRIBUTE_HANDLE_LIST = (IntPtr)0x00020002;

    // ProcThreadAttributeChildProcessPolicy(=14) | PROC_THREAD_ATTRIBUTE_INPUT(=0x00020000)
    internal const IntPtr PROC_THREAD_ATTRIBUTE_CHILD_PROCESS_POLICY = (IntPtr)0x0002000E;
    // 子プロセスの新規作成(CreateProcess)自体を拒否させる(プロセスは道連れにしない)
    internal const uint PROCESS_CREATION_CHILD_PROCESS_RESTRICTED = 0x00000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SECURITY_CAPABILITIES
    {
        public IntPtr AppContainerSid;
        public IntPtr Capabilities;
        public uint CapabilityCount;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SID_AND_ATTRIBUTES
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    internal const uint SE_GROUP_ENABLED = 0x00000004;

    // APPLICATION PACKAGE AUTHORITY\internetClient のwell-knownケーパビリティSID。
    // これをAppContainerトークンに含めないと、srm独自のWFP許可ルールとは無関係に
    // Windows組み込みのAppContainerネットワーク隔離機構が全アウトバウンド通信を
    // ブロックする（allow_hostsを指定しても実際には一切繋がらない）。
    internal const string CAPABILITY_INTERNET_CLIENT_SID = "S-1-15-3-1";
}

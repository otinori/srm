using System.Runtime.InteropServices;

namespace Srm.Runtime.Native;

// restricted-account-app-isolation: 【実機で判明した重要な結果】別ユーザーのトークンで
// コンソールアプリ（cmd.exe）をCreateProcessWithTokenWで起動すると、conhost.exeが
// 一度も生成されないままLpcReply待ちで無期限にハングすることを確認した
// （STARTUPINFO.lpDesktop="WinSta0\Default"を明示しても解消しない）。原因は、
// 対象アカウントが対話的ウィンドウステーション(WinSta0)・デスクトップ(Default)への
// アクセス権を既定で持たないこと。PsExecの`-i`オプション等が使う既知の回避策と
// 同じく、対象アカウントのSIDへ両オブジェクトの明示的なDACLエントリを付与する
// 必要がある。
internal static class UserObjectNative
{
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr OpenWindowStationW(string lpszWinSta, [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseWindowStation(IntPtr hWinSta);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr OpenDesktopW(string lpszDesktop, uint dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetUserObjectSecurity(
        IntPtr hObj, ref uint pSIRequested, IntPtr pSID, uint nLength, out uint lpnLengthNeeded);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetUserObjectSecurity(IntPtr hObj, ref uint pSIRequested, IntPtr pSID);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InitializeSecurityDescriptor(IntPtr pSecurityDescriptor, uint dwRevision);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetSecurityDescriptorDacl(
        IntPtr pSecurityDescriptor,
        [MarshalAs(UnmanagedType.Bool)] bool bDaclPresent,
        IntPtr pDacl,
        [MarshalAs(UnmanagedType.Bool)] bool bDaclDefaulted);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSecurityDescriptorDacl(
        IntPtr pSecurityDescriptor,
        [MarshalAs(UnmanagedType.Bool)] out bool bDaclPresent,
        out IntPtr pDacl,
        [MarshalAs(UnmanagedType.Bool)] out bool bDaclDefaulted);

    internal const uint READ_CONTROL = 0x00020000;
    internal const uint WRITE_DAC = 0x00040000;
    internal const uint STANDARD_RIGHTS_REQUIRED = 0x000F0000;

    // WinSta0/Defaultへ「対話的に使う」上で必要な全権限。個別ビットを絞り込む価値は
    // 薄い（対象は共有インフラのウィンドウステーション/デスクトップそのものであり、
    // allow_pathsのような機密データではない）ため、PsExec等の既知の実装と同じく
    // フルアクセスを付与する。
    internal const uint WINSTA_ALL_ACCESS = STANDARD_RIGHTS_REQUIRED | 0x37F;
    internal const uint DESKTOP_ALL_ACCESS = STANDARD_RIGHTS_REQUIRED | 0x01FF;

    internal const uint SECURITY_DESCRIPTOR_REVISION = 1;
    // 【実機で判明した重要な結果】WinNT.hのSECURITY_DESCRIPTOR_MIN_LENGTH(=sizeof(
    // SECURITY_DESCRIPTOR))は32ビット時代の値20を単純に転記できない。x64では
    // SECURITY_DESCRIPTOR構造体がOwner/Group/Sacl/Daclの4つの8バイトポインタを
    // 埋め込む絶対形式であり、ヘッダー4バイト+アラインメント調整を含めて約40バイトを
    // 要する。20バイトしか確保しないと InitializeSecurityDescriptor/
    // SetSecurityDescriptorDacl がバッファ外を書き込みヒープを破壊し、別のテストが
    // ヒープを操作したタイミングでのみプロセスがクラッシュするという再現しにくい
    // 不具合を引き起こした（テストスイート全体を回すと再現、単体では再現しない、
    // という形で発覚）。安全余裕を持って64バイト確保する。
    internal const int SECURITY_DESCRIPTOR_MIN_LENGTH = 64;
}

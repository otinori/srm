using System.Diagnostics;
using System.Runtime.InteropServices;

// srm run が AppContainer 化して起動したプロセス自身と、それが素の Process.Start
// （SECURITY_CAPABILITIES属性なし）で起動した子・孫プロセスの IsAppContainer /
// AppContainerSid / 特権数を、cmd.exe や whoami 等を一切経由せず直接自己申告する。
// 引数なし=top、"child"=top が起動、"grandchild"=childが起動。
// ログは実行時のカレントディレクトリ（srmのworking_directory）に追記する。

// "sleep <秒>": ネットワーク・コンソール入力に一切触れずに指定秒数生存するだけのモード。
// srm list / srm stop の検証用（ping/timeoutはAppContainer内でアクセス拒否になるため使えない）。
if (args.Length > 0 && args[0] == "sleep")
{
    var seconds = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 30;
    Thread.Sleep(TimeSpan.FromSeconds(seconds));
    return;
}

var role = args.Length > 0 ? args[0] : "top";
Report(role);

var next = role switch
{
    "top" => "child",
    "child" => "grandchild",
    _ => null,
};

if (next != null)
{
    try
    {
        var exePath = Process.GetCurrentProcess().MainModule!.FileName;
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = next,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        using var p = Process.Start(psi);
        if (p is null)
        {
            Append($"role={role} pid={Environment.ProcessId} SPAWN_FAILED: Process.Start returned null");
        }
        else if (!p.WaitForExit(15000))
        {
            Append($"role={role} pid={Environment.ProcessId} SPAWN_WARN: child(role={next}) pid={p.Id} did not exit within 15s");
        }
    }
    catch (Exception ex)
    {
        Append($"role={role} pid={Environment.ProcessId} SPAWN_FAILED: {ex.GetType().Name}: {ex.Message}");
    }
}

return;

static void Report(string role)
{
    var pid = Environment.ProcessId;
    var procHandle = NativeMethods.GetCurrentProcess();

    if (!NativeMethods.OpenProcessToken(procHandle, NativeMethods.TOKEN_QUERY, out var token))
    {
        Append($"role={role} pid={pid} OpenProcessToken_FAILED err={Marshal.GetLastWin32Error()}");
        return;
    }

    try
    {
        var isAppContainer = QueryUInt32(token, NativeMethods.TokenIsAppContainer, out var err1);
        var sidStr = QueryAppContainerSid(token, out var err2);
        var privCount = QueryPrivilegeCount(token, out var err3);
        var integrity = QueryIntegritySid(token, out var err4);

        Append($"role={role} pid={pid} IsAppContainer={isAppContainer}(err={err1}) " +
               $"AppContainerSid={sidStr}(err={err2}) PrivilegeCount={privCount}(err={err3}) " +
               $"IntegritySid={integrity}(err={err4})");
    }
    finally
    {
        NativeMethods.CloseHandle(token);
    }
}

static uint QueryUInt32(IntPtr token, int cls, out int err)
{
    var buf = Marshal.AllocHGlobal(4);
    try
    {
        if (NativeMethods.GetTokenInformation(token, cls, buf, 4, out _))
        {
            err = 0;
            return unchecked((uint)Marshal.ReadInt32(buf));
        }
        err = Marshal.GetLastWin32Error();
        return 0xFFFFFFFF;
    }
    finally
    {
        Marshal.FreeHGlobal(buf);
    }
}

static string QueryAppContainerSid(IntPtr token, out int err)
{
    // TOKEN_APPCONTAINER_INFORMATION はヘッダにPSIDポインタを持つが、GetTokenInformationは
    // 実際のSIDバイト列も同じバッファ内に続けて書き込むため、sizeof(PSID)だけでは
    // ERROR_INSUFFICIENT_BUFFER(122)になる。TokenPrivileges等と同様に2回呼び出しでサイズを取得する。
    NativeMethods.GetTokenInformation(token, NativeMethods.TokenAppContainerSid, IntPtr.Zero, 0, out var needed);
    if (needed == 0)
    {
        err = Marshal.GetLastWin32Error();
        return "(size query failed)";
    }
    var buf = Marshal.AllocHGlobal((int)needed);
    try
    {
        if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenAppContainerSid, buf, needed, out _))
        {
            err = Marshal.GetLastWin32Error();
            return "(query failed)";
        }
        err = 0;
        var sidPtr = Marshal.ReadIntPtr(buf);
        return SidToString(sidPtr);
    }
    finally
    {
        Marshal.FreeHGlobal(buf);
    }
}

static string QueryIntegritySid(IntPtr token, out int err)
{
    // TOKEN_MANDATORY_LABEL は SID_AND_ATTRIBUTES { PSID Sid; DWORD Attributes; } の可変長構造体。
    NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel, IntPtr.Zero, 0, out var needed);
    if (needed == 0)
    {
        err = Marshal.GetLastWin32Error();
        return "(size query failed)";
    }
    var buf = Marshal.AllocHGlobal((int)needed);
    try
    {
        if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel, buf, needed, out _))
        {
            err = Marshal.GetLastWin32Error();
            return "(query failed)";
        }
        err = 0;
        var sidPtr = Marshal.ReadIntPtr(buf); // 構造体の先頭フィールド = PSID
        return SidToString(sidPtr);
    }
    finally
    {
        Marshal.FreeHGlobal(buf);
    }
}

static string SidToString(IntPtr sidPtr)
{
    if (sidPtr == IntPtr.Zero) return "(null)";
    if (!NativeMethods.ConvertSidToStringSidW(sidPtr, out var strPtr))
        return $"(convert failed err={Marshal.GetLastWin32Error()})";
    try
    {
        return Marshal.PtrToStringUni(strPtr) ?? "(null string)";
    }
    finally
    {
        NativeMethods.LocalFree(strPtr);
    }
}

static int QueryPrivilegeCount(IntPtr token, out int err)
{
    NativeMethods.GetTokenInformation(token, NativeMethods.TokenPrivileges, IntPtr.Zero, 0, out var needed);
    if (needed == 0)
    {
        err = Marshal.GetLastWin32Error();
        return -1;
    }
    var buf = Marshal.AllocHGlobal((int)needed);
    try
    {
        if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenPrivileges, buf, needed, out _))
        {
            err = Marshal.GetLastWin32Error();
            return -1;
        }
        err = 0;
        return Marshal.ReadInt32(buf); // TOKEN_PRIVILEGES.PrivilegeCount が先頭DWORD
    }
    finally
    {
        Marshal.FreeHGlobal(buf);
    }
}

static void Append(string line)
{
    var path = Path.Combine(Environment.CurrentDirectory, "tokencheck-log.txt");
    var text = $"{DateTime.Now:O} {line}{Environment.NewLine}";
    File.AppendAllText(path, text);
    Console.WriteLine(line);
}

internal static class NativeMethods
{
    internal const uint TOKEN_QUERY = 0x0008;
    internal const int TokenPrivileges = 3;
    internal const int TokenIntegrityLevel = 25;
    internal const int TokenIsAppContainer = 29;
    internal const int TokenAppContainerSid = 31;

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool GetTokenInformation(
        IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation,
        uint tokenInformationLength, out uint returnLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr stringSid);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr LocalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);
}

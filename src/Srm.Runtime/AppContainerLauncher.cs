using System.ComponentModel;
using System.Runtime.InteropServices;
using Srm.PolicyEngine.Models;
using Srm.Runtime.Native;

namespace Srm.Runtime;

public class LaunchResult
{
    public required int ProcessId { get; init; }
    public required IntPtr ProcessHandle { get; init; }
    public required IntPtr ThreadHandle { get; init; }
}

public class AppContainerLauncher
{
    public unsafe LaunchResult Launch(
        PolicyModel policy,
        IntPtr sidAppContainer,
        string? workingDirectory = null,
        bool redirectStdioToNul = false,
        IReadOnlyDictionary<string, string>? extraEnvironmentVariables = null,
        bool forceInternetCapability = false)
    {
        var exe = policy.Application.Executable;
        var args = policy.Application.Arguments;
        var wd = workingDirectory ?? policy.Application.WorkingDirectory;

        var commandLine = string.IsNullOrWhiteSpace(args)
            ? $"\"{exe}\""
            : $"\"{exe}\" {args}";

        IntPtr attrList = IntPtr.Zero;
        var capsPtr = IntPtr.Zero;
        var childPolicyPtr = IntPtr.Zero;
        var internetClientSid = IntPtr.Zero;
        var capabilitiesPtr = IntPtr.Zero;
        var nulHandle = IntPtr.Zero;
        var handleListPtr = IntPtr.Zero;
        var environmentBlockPtr = IntPtr.Zero;
        var allowChildProcesses = policy.Process.AllowChildProcesses;
        var attributeCount = (allowChildProcesses ? 1 : 2) + (redirectStdioToNul ? 1 : 0);

        try
        {
            // allow_hostsが1つでも指定されていれば、そのAppContainerに
            // internetClientケーパビリティを与える。これが無いと、srm独自のWFP許可
            // ルールとは無関係にWindows組み込みのAppContainerネットワーク隔離が
            // 全アウトバウンド通信をブロックしてしまう。allow_hostsが空のポリシーは
            // 元々ネットワーク完全遮断を意図しているため、ケーパビリティを与えない。
            //
            // resource-access-audit-logging: `srm audit`はallow_hostsによる拒否を
            // 行わず、対象アプリが実際にどこへ接続しようとするかをETWで観測したい
            // ため、allow_hostsの内容に関わらずforceInternetCapability=trueで
            // ケーパビリティを強制的に付与できるようにする（AuditOperation経由でのみ
            // trueを渡す。srm run経由の呼び出しは常にfalseのまま、既存動作を変えない）。
            var caps = new AppContainerNative.SECURITY_CAPABILITIES
            {
                AppContainerSid = sidAppContainer,
                Capabilities = IntPtr.Zero,
                CapabilityCount = 0,
                Reserved = 0,
            };

            if (policy.Network.AllowHosts.Count > 0 || forceInternetCapability)
            {
                if (!AclNative.ConvertStringSidToSidW(AppContainerNative.CAPABILITY_INTERNET_CLIENT_SID, out internetClientSid) || internetClientSid == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "internetClientケーパビリティSIDの変換に失敗しました");

                var sidAndAttrs = new AppContainerNative.SID_AND_ATTRIBUTES
                {
                    Sid = internetClientSid,
                    Attributes = AppContainerNative.SE_GROUP_ENABLED,
                };
                capabilitiesPtr = Marshal.AllocHGlobal(Marshal.SizeOf(sidAndAttrs));
                Marshal.StructureToPtr(sidAndAttrs, capabilitiesPtr, false);

                caps.Capabilities = capabilitiesPtr;
                caps.CapabilityCount = 1;
            }

            // PROC_THREAD_ATTRIBUTE_LIST のサイズを取得
            IntPtr listSize = IntPtr.Zero;
            AppContainerNative.InitializeProcThreadAttributeList(IntPtr.Zero, attributeCount, 0, ref listSize);
            attrList = Marshal.AllocHGlobal(listSize);
            if (!AppContainerNative.InitializeProcThreadAttributeList(attrList, attributeCount, 0, ref listSize))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "ProcThreadAttributeListの初期化に失敗しました");

            capsPtr = Marshal.AllocHGlobal(Marshal.SizeOf(caps));
            Marshal.StructureToPtr(caps, capsPtr, false);

            if (!AppContainerNative.UpdateProcThreadAttribute(
                    attrList,
                    0,
                    AppContainerNative.PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES,
                    capsPtr,
                    (IntPtr)Marshal.SizeOf(caps),
                    IntPtr.Zero,
                    IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "AppContainerセキュリティ属性の設定に失敗しました");

            // process.allow_child_processes: false の場合、対象プロセス自身がCreateProcessで
            // 新しい子プロセスを作成すること自体を拒否させる（Job Objectの
            // JOB_OBJECT_LIMIT_ACTIVE_PROCESSと違い、制限超過時に対象プロセス自体を
            // 道連れにして殺すことはない。子の起動だけがエラーになる）。
            if (!allowChildProcesses)
            {
                childPolicyPtr = Marshal.AllocHGlobal(sizeof(uint));
                Marshal.WriteInt32(childPolicyPtr, unchecked((int)AppContainerNative.PROCESS_CREATION_CHILD_PROCESS_RESTRICTED));

                if (!AppContainerNative.UpdateProcThreadAttribute(
                        attrList,
                        0,
                        AppContainerNative.PROC_THREAD_ATTRIBUTE_CHILD_PROCESS_POLICY,
                        childPolicyPtr,
                        (IntPtr)sizeof(uint),
                        IntPtr.Zero,
                        IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "子プロセス制限属性の設定に失敗しました");
            }

            var siEx = new AppContainerNative.STARTUPINFOEX
            {
                StartupInfo = new AppContainerNative.STARTUPINFO
                {
                    cb = Marshal.SizeOf<AppContainerNative.STARTUPINFOEX>(),
                },
                lpAttributeList = attrList,
            };

            // CREATE_NEW_CONSOLE を付けるとAppContainerからのコンソール新規割り当てが
            // 応答なしでハングする（要：再現確認・原因はconhost/csrssとのALPC交渉が
            // 制限されたAppContainerトークンで進まないことだと推測）。フラグを付けなければ
            // 子プロセスは呼び出し元(srm.exe)のコンソールをそのまま継承して出力する。
            uint flags = AppContainerNative.EXTENDED_STARTUPINFO_PRESENT;

            // 診断用（2026-07-01・一時的）: 環境変数 SRM_DIAG_NEW_CONSOLE=1 のときだけ
            // CREATE_NEW_CONSOLE を付けてハングを意図的に再現し、Process Explorer等で
            // スレッドスタックを観測するための一時的なフック。根本原因調査が終わったら
            // このブロックごと削除すること。
            if (Environment.GetEnvironmentVariable("SRM_DIAG_NEW_CONSOLE") == "1")
                flags |= AppContainerNative.CREATE_NEW_CONSOLE;

            var inheritHandles = false;

            // MCPサーバー（Srm.Mcp）経由のsrm_runでは、呼び出し元(Srm.Mcp.exe)の
            // stdoutはJSON-RPC伝送路そのものであり、対象アプリのコンソール出力が
            // そこに混入するとプロトコルが壊れる（DC-017 review_trigger、実機で
            // 再現確認済み）。CREATE_NEW_CONSOLEはAppContainerでハングするため使えず
            // （上記コメント参照）、代わりにSTARTF_USESTDHANDLESでNULデバイスへ
            // 明示的にリダイレクトする。PROC_THREAD_ATTRIBUTE_HANDLE_LISTで
            // 継承するハンドルをNULハンドルのみに限定し、bInheritHandles=trueに
            // した際にSrm.Mcp自身のstdio等の無関係なハンドルまで子プロセスへ
            // 漏れないようにする。
            if (redirectStdioToNul)
            {
                var sa = new Kernel32Native.SECURITY_ATTRIBUTES
                {
                    nLength = Marshal.SizeOf<Kernel32Native.SECURITY_ATTRIBUTES>(),
                    bInheritHandle = true,
                    lpSecurityDescriptor = IntPtr.Zero,
                };
                nulHandle = Kernel32Native.CreateFile(
                    "NUL",
                    Kernel32Native.GENERIC_READ | Kernel32Native.GENERIC_WRITE,
                    Kernel32Native.FILE_SHARE_READ | Kernel32Native.FILE_SHARE_WRITE,
                    ref sa,
                    Kernel32Native.OPEN_EXISTING,
                    0,
                    IntPtr.Zero);
                if (nulHandle == Kernel32Native.InvalidHandleValue)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "NULデバイスのオープンに失敗しました");

                siEx.StartupInfo.dwFlags |= AppContainerNative.STARTF_USESTDHANDLES;
                siEx.StartupInfo.hStdInput = nulHandle;
                siEx.StartupInfo.hStdOutput = nulHandle;
                siEx.StartupInfo.hStdError = nulHandle;

                handleListPtr = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(handleListPtr, nulHandle);
                if (!AppContainerNative.UpdateProcThreadAttribute(
                        attrList,
                        0,
                        AppContainerNative.PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
                        handleListPtr,
                        (IntPtr)IntPtr.Size,
                        IntPtr.Zero,
                        IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "ハンドル継承リストの設定に失敗しました");

                inheritHandles = true;
            }

            // channel-d-guest-mcp-bridge: mcp.allow_serversを指定したポリシーは
            // SRM_MCP_CONTROL_DIR/SRM_MCP_SERVERSを対象プロセス自身の環境変数として
            // 渡す（ゲスト側スタブがこのプロセスの子として起動された場合に継承される
            // ことを期待する。design.mdのtask 4.2）。lpEnvironmentにIntPtr.Zeroを
            // 渡すと「呼び出し元(srm.exe)の環境をそのまま継承」になり追加ができない
            // ため、指定がある場合のみ呼び出し元の環境+追加分をマージした環境ブロックを
            // 明示的に構築する。
            if (extraEnvironmentVariables is { Count: > 0 })
            {
                environmentBlockPtr = BuildEnvironmentBlock(extraEnvironmentVariables);
                flags |= AppContainerNative.CREATE_UNICODE_ENVIRONMENT;
            }

            if (!AppContainerNative.CreateProcess(
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles,
                    flags,
                    environmentBlockPtr,
                    string.IsNullOrWhiteSpace(wd) ? null : wd,
                    ref siEx,
                    out var pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"プロセスの起動に失敗しました: {exe}\n" +
                    "次を確認してください: 実行ファイルのパスが正しいか、ファイルが存在するか、パスにACLが設定されているか");

            return new LaunchResult
            {
                ProcessId = pi.dwProcessId,
                ProcessHandle = pi.hProcess,
                ThreadHandle = pi.hThread,
            };
        }
        finally
        {
            if (attrList != IntPtr.Zero)
            {
                AppContainerNative.DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
            }
            if (capsPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(capsPtr);
            if (childPolicyPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(childPolicyPtr);
            if (capabilitiesPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(capabilitiesPtr);
            if (internetClientSid != IntPtr.Zero)
                AclNative.LocalFree(internetClientSid);
            if (handleListPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(handleListPtr);
            // nulHandleはCreateProcess成功後は子プロセスが独立して保持するため、
            // ここでCloseHandleしてもホスト側の参照が閉じるだけで子プロセス側には
            // 影響しない（Win32ハンドル継承の標準的な後始末）。
            if (nulHandle != IntPtr.Zero)
                Kernel32Native.CloseHandle(nulHandle);
            if (environmentBlockPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(environmentBlockPtr);
        }
    }

    // 呼び出し元(srm.exe)自身の環境変数を土台にし、extraが同名キーを持つ場合は上書きする
    // （呼び出し元を継承しつつ追加する、というlpEnvironment=IntPtr.Zeroの既定動作に
    // 近い挙動を保つため）。ブロックの構築自体はEnvironmentBlockBuilderへ委譲する
    // （RestrictedAccountLauncherと共有、土台の取得方法だけがランチャーごとに異なる）。
    private static IntPtr BuildEnvironmentBlock(IReadOnlyDictionary<string, string> extra)
    {
        var baseEnv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            baseEnv[(string)entry.Key] = (string)(entry.Value ?? "");

        return EnvironmentBlockBuilder.Build(baseEnv, extra);
    }
}

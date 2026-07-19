using System.Runtime.InteropServices;
using Srm.Runtime.Native;

namespace Srm.Runtime.Interaction;

// Tier2（Windows Sandbox）向け: SandboxLauncherが起動したWindowsSandbox.exeホスト
// プロセス（RunningApp.Pid）がホスト上に開く、ゲストデスクトップをRDPベースで
// レンダリングするウィンドウを解決する（DC-017 チャネルA）。ゲスト内`--nested`側の
// 変更は不要— ホスト側でこのウィンドウにSendInputすると、既存のRDP転送機構が
// そのままゲストへ届ける。
//
// 未検証事項（DC-017 review_trigger）: 実際にレンダリングウィンドウを持つのが
// WindowsSandbox.exe自身か、それが起動する別プロセス（WindowsSandboxClient.exe等）
// かは実機PoC待ち。前者しか見ない設計だと後者が正解だった場合に全滅するため、
// 保守的にホストプロセスとその子孫プロセス全体を候補として扱う。
public class Tier2SandboxWindowResolver
{
    // Windows Sandboxの実行ファイル群は %SystemRoot%\System32 配下にある想定
    // （WindowsSandbox.exe自体はここに存在することが確認されている。子プロセスが
    // 別ディレクトリの場合はこのチェックごと見直しが必要 — review_trigger参照）。
    public static readonly string ExpectedDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System));

    public const string ExpectedExecutableNameContains = "WindowsSandbox";

    public HashSet<int> GetAllowedPids(RunningApp app)
    {
        var pids = new HashSet<int> { app.Pid };
        CollectDescendants(app.Pid, pids);
        return pids;
    }

    public List<IntPtr> ResolveCandidateWindows(HashSet<int> allowedPids)
    {
        var result = new List<IntPtr>();
        User32Native.EnumWindows((hwnd, _) =>
        {
            User32Native.GetWindowThreadProcessId(hwnd, out var ownerPid);
            if (allowedPids.Contains((int)ownerPid) && User32Native.IsWindowVisible(hwnd))
                result.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static void CollectDescendants(int rootPid, HashSet<int> collected)
    {
        var snapshot = Kernel32Native.CreateToolhelp32Snapshot(Kernel32Native.TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return;

        try
        {
            var entries = new List<(uint Pid, uint ParentPid)>();
            var entry = new Kernel32Native.PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<Kernel32Native.PROCESSENTRY32>() };
            if (!Kernel32Native.Process32First(snapshot, ref entry)) return;
            do
            {
                entries.Add((entry.th32ProcessID, entry.th32ParentProcessID));
            } while (Kernel32Native.Process32Next(snapshot, ref entry));

            var frontier = new Queue<uint>();
            frontier.Enqueue((uint)rootPid);
            while (frontier.Count > 0)
            {
                var parent = frontier.Dequeue();
                foreach (var (pid, ppid) in entries)
                {
                    if (ppid == parent && collected.Add((int)pid))
                        frontier.Enqueue(pid);
                }
            }
        }
        finally
        {
            Kernel32Native.CloseHandle(snapshot);
        }
    }
}

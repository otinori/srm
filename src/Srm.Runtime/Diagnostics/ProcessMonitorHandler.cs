using System.Runtime.InteropServices;
using Srm.Runtime.Sandbox;

namespace Srm.Runtime.Diagnostics;

// tier2-channel-c-mapped-folder: ゲスト内`--nested`のバックグラウンドループから
// 継続的に呼ばれる本体ロジック（FocusRequestHandlerと同じ「直接テスト可能」
// パターン）。DiagCollectorと違い前後差分は取らず、呼ばれた時点の累積値を
// そのまま返す（design.md Decision 5、継続ポーリング前提の軽量化。レートが
// 必要ならホスト側が連続する2スナップショットの差分を取る）。
//
// 【DC-023/DC-025で発見・DC-026で解決】実機検証（Tier2、tier2.app_container:
// true）で、`System.Diagnostics.Process.GetProcessById`/`GetProcesses`が対象
// AppContainerプロセスのPIDを解決できず一貫して失敗することが判明した。切り分けの
// 結果、生の`OpenProcess`（PROCESS_QUERY_LIMITED_INFORMATION）はエラーコード0
// （成功）で対象PIDのハンドルを取得できるにもかかわらず、`Process.GetProcesses()`
// による列挙にはそのPIDが一切現れないという矛盾した結果を確認した。つまりOS
// レベルのアクセス拒否ではなく、.NETの`Process`クラス内部の列挙ロジック
// （`GetProcessById`は内部で`GetProcesses()`相当の列挙を行い対象PIDの有無を
// 確認してから例外を投げる実装になっている）がAppContainerプロセスを認識できない
// という.NET側の制約だと判明した。監視対象PIDは常に既知（JobObjectManagerから
// 取得済み）で「未知のPIDを列挙して発見する」必要が無いため、`Process`クラスの
// 列挙に依存せず、既知のPIDに対して直接`OpenProcess`する生Win32 APIベースの
// 実装に置き換えて回避した（DiagCollectorも同じ問題を抱えており同様に修正した）。
//
// なお、ハンドル数・スレッド数・ワーキングセットは、AppContainerプロセスに対しては
// `GetProcessHandleCount`/`GetProcessMemoryInfo`/スレッド列挙のいずれもエラーを
// 返さず「成功」するにもかかわらず、実際の値ではなく最小値（0または固定の
// 微小値）を返すことを実機で確認した。`PROCESS_QUERY_INFORMATION`（フル権限）
// でのOpenProcessも成功し、`SeDebugPrivilege`を明示的に有効化しても変化せず、
// さらにWin32ラッパーを経由せず`NtQueryInformationProcess`
// （`ProcessHandleCount`情報クラス）を直接呼んでも`STATUS_INFO_LENGTH_MISMATCH`
// (0xC0000004)を返す（しかも`ReturnLength`が0のまま、通常この状態異常時に
// 期待される「必要なバッファ長を教える」挙動すら起きない）ことを確認した。
// これによりOS内部の最下層APIレベルでもAppContainerプロセスに対するこの種の
// 照会が構造的に拒否されている（バッファサイズの問題ではない）ことが実機で
// 裏付けられた。srmの権限不足ではなく、AppContainer分離自体がこれらの詳細を
// 外部の照会者（管理者/SYSTEM権限であっても）から隠す設計になっていると
// 結論づけ、これ以上の追加調査は投資対効果に見合わないと判断して打ち切った
// （DC-026参照）。CPU時間（`GetProcessTimes`）だけはAppContainerプロセスに
// 対しても正しい値を返すことを確認済みで、DC-016以来最も重要視されてきた
// 指標であるため実用上の価値は失われていない。
public class ProcessMonitorHandler
{
    public ProcessMonitorResultModel Handle(string requestId, IEnumerable<int> targetProcessIds)
    {
        var snapshots = new List<ProcessSnapshotModel>();
        var failureReasons = new List<string>();
        foreach (var pid in targetProcessIds)
        {
            var snapshot = TryTakeSnapshot(pid, out var failureReason);
            if (snapshot != null)
                snapshots.Add(snapshot);
            else if (failureReason != null)
                failureReasons.Add($"PID {pid}: {failureReason}");
        }

        // プロセスツリーのうち一部（既に終了した子プロセス等）が解決できないのは
        // 通常のことなので失敗にしない。全滅した場合のみSuccess=falseとする
        // （task 3.3: 対象プロセスが解決できない・消失した場合のエラーハンドリング）。
        if (snapshots.Count == 0)
        {
            return new ProcessMonitorResultModel
            {
                RequestId = requestId,
                Success = false,
                Reason = "監視対象のプロセスが1つも解決できませんでした: " + string.Join("; ", failureReasons),
                TimestampUtc = DateTime.UtcNow,
            };
        }

        return new ProcessMonitorResultModel
        {
            RequestId = requestId,
            Success = true,
            TimestampUtc = DateTime.UtcNow,
            Processes = snapshots,
        };
    }

    private static ProcessSnapshotModel? TryTakeSnapshot(int pid, out string? failureReason)
    {
        var hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (hProcess == IntPtr.Zero)
        {
            failureReason = $"OpenProcessに失敗しました（Win32エラーコード: {Marshal.GetLastWin32Error()}）";
            return null;
        }

        try
        {
            if (!GetProcessTimes(hProcess, out _, out _, out var kernelTime, out var userTime))
            {
                failureReason = $"GetProcessTimesに失敗しました（Win32エラーコード: {Marshal.GetLastWin32Error()}）";
                return null;
            }

            var counters = new PROCESS_MEMORY_COUNTERS { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS>() };
            GetProcessMemoryInfo(hProcess, out counters, counters.cb);
            GetProcessHandleCount(hProcess, out var handleCount);

            failureReason = null;
            return new ProcessSnapshotModel
            {
                ProcessId = pid,
                TotalProcessorTimeMs = FileTimeToMilliseconds(kernelTime) + FileTimeToMilliseconds(userTime),
                ThreadCount = CountThreads(pid),
                HandleCount = (int)handleCount,
                WorkingSetBytes = (long)counters.WorkingSetSize,
            };
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    private static double FileTimeToMilliseconds(FILETIME ft)
    {
        var ticks = ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
        return ticks / 10000.0; // FILETIMEは100ns単位
    }

    // スレッド数はToolhelpスナップショット（CreateToolhelp32Snapshot）で数える。
    // Process.GetProcesses()系とは別の列挙APIであり、実機でAppContainer
    // プロセスに対しても（この情報自体がAppContainer分離により0を返す場合が
    // あることを除けば）正しく機能することを確認済み。取得に失敗した場合は
    // 0を返す（致命的ではない付随情報のため、これだけで全体を失敗にしない）。
    private static int CountThreads(int pid)
    {
        var hSnapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
        if (hSnapshot == IntPtr.Zero || hSnapshot == InvalidHandleValue)
            return 0;

        try
        {
            var entry = new THREADENTRY32 { dwSize = (uint)Marshal.SizeOf<THREADENTRY32>() };
            var count = 0;
            if (!Thread32First(hSnapshot, ref entry))
                return 0;

            do
            {
                if (entry.th32OwnerProcessID == (uint)pid)
                    count++;
            } while (Thread32Next(hSnapshot, ref entry));

            return count;
        }
        finally
        {
            CloseHandle(hSnapshot);
        }
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TH32CS_SNAPTHREAD = 0x00000004;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        IntPtr hProcess, out FILETIME lpCreationTime, out FILETIME lpExitTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessHandleCount(IntPtr hProcess, out uint pdwHandleCount);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(IntPtr hProcess, out PROCESS_MEMORY_COUNTERS counters, uint cb);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Thread32First(IntPtr hSnapshot, ref THREADENTRY32 lpte);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Thread32Next(IntPtr hSnapshot, ref THREADENTRY32 lpte);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct THREADENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ThreadID;
        public uint th32OwnerProcessID;
        public int tpBasePri;
        public int tpDeltaPri;
        public uint dwFlags;
    }
}

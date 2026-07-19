using System.Runtime.InteropServices;
using Srm.Runtime.Sandbox;

namespace Srm.Runtime.Diagnostics;

// DC-016（claude-code CLIをAppContainer内で-p実行するとカーネルモードCPUを消費し
// 続ける未解決バグ）の実機再調査を容易にするための軽量サンプラー。指定PIDのCPU時間
// （ユーザー/カーネル）とページフォールト数を、指定した経過時間の前後でサンプリングし、
// 差分から秒あたりのレート・比率を計算する。WPRのような完全なスタックトレース採取は
// 行わず、Procmonの「Process Profiling」で確認していた数値を都度手動操作せずに
// 取得できるようにすることが目的（review_trigger参照）。
// Tier1（ホストと同一セッション）・Tier2（ゲスト内`--nested`）の両方から、
// 対象PIDのみを渡して同じロジックを再利用する。
//
// 【DC-023/DC-025で発見・DC-026で解決】実機検証（Tier2、tier2.app_container:
// true）で、`System.Diagnostics.Process.GetProcessById`が対象AppContainer
// プロセスのPIDを解決できず一貫して失敗することが判明した（詳細は
// ProcessMonitorHandlerのコメント参照。.NETの`Process`クラス内部の列挙ロジックが
// AppContainerプロセスを認識できないことが原因で、OSレベルのアクセス拒否ではない）。
// 対象PIDは常に呼び出し元から渡される既知の値のため、`Process`クラスの列挙に
// 依存しない生Win32 APIベースの実装に置き換えて回避した。
public class DiagCollector
{
    public DiagResultModel Handle(int targetProcessId, DiagRequestModel request)
    {
        var hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)targetProcessId);
        if (hProcess == IntPtr.Zero)
            return Fail(request, targetProcessId, $"PID {targetProcessId} のプロセスが見つかりません（既に終了している可能性があります）");

        try
        {
            if (!TryTakeSample(hProcess, targetProcessId, out var before))
                return Fail(request, targetProcessId, $"PID {targetProcessId} のプロセス情報取得に失敗しました");

            Thread.Sleep(Math.Max(request.SampleWindowMs, 100));

            if (!TryTakeSample(hProcess, targetProcessId, out var after))
                return Fail(request, targetProcessId, "サンプリング中に対象プロセスの情報取得に失敗しました（終了した可能性があります）");

            var elapsedSeconds = (after.Timestamp - before.Timestamp).TotalSeconds;
            if (elapsedSeconds <= 0) elapsedSeconds = request.SampleWindowMs / 1000.0;

            var userDeltaSeconds = (after.UserTime - before.UserTime).TotalSeconds;
            var kernelDeltaSeconds = (after.KernelTime - before.KernelTime).TotalSeconds;
            var pageFaultDelta = after.PageFaultCount - before.PageFaultCount;

            return new DiagResultModel
            {
                RequestId = request.RequestId,
                Success = true,
                ProcessId = targetProcessId,
                SampleWindowMs = (int)Math.Round(elapsedSeconds * 1000),
                UserTimePercent = Math.Round(userDeltaSeconds / elapsedSeconds * 100, 1),
                KernelTimePercent = Math.Round(kernelDeltaSeconds / elapsedSeconds * 100, 1),
                PageFaultsPerSec = Math.Round(pageFaultDelta / elapsedSeconds, 1),
                ThreadCount = after.ThreadCount,
                HandleCount = after.HandleCount,
            };
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    private static DiagResultModel Fail(DiagRequestModel request, int processId, string reason) => new()
    {
        RequestId = request.RequestId,
        Success = false,
        Reason = reason,
        ProcessId = processId,
    };

    private readonly record struct Sample(
        DateTime Timestamp, TimeSpan UserTime, TimeSpan KernelTime, long PageFaultCount, int ThreadCount, int HandleCount);

    // ハンドル数・スレッド数・ページフォールト数は、AppContainerプロセスに対しては
    // 該当するWin32 APIがエラーを返さず「成功」するにもかかわらず実際の値ではなく
    // 最小値を返すことを実機で確認済み（ProcessMonitorHandlerのコメント参照）。
    // CPU時間（GetProcessTimes）だけは正しい値を返すため、DC-016の主目的である
    // CPU使用率診断としての実用上の価値は失われていない。
    private static bool TryTakeSample(IntPtr hProcess, int pid, out Sample sample)
    {
        if (!GetProcessTimes(hProcess, out _, out _, out var kernelTime, out var userTime))
        {
            sample = default;
            return false;
        }

        var counters = new PROCESS_MEMORY_COUNTERS { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS>() };
        GetProcessMemoryInfo(hProcess, out counters, counters.cb);
        GetProcessHandleCount(hProcess, out var handleCount);

        sample = new Sample(
            DateTime.UtcNow,
            TimeSpan.FromMilliseconds(FileTimeToMilliseconds(userTime)),
            TimeSpan.FromMilliseconds(FileTimeToMilliseconds(kernelTime)),
            counters.PageFaultCount,
            CountThreads(pid),
            (int)handleCount);
        return true;
    }

    private static double FileTimeToMilliseconds(FILETIME ft)
    {
        var ticks = ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
        return ticks / 10000.0; // FILETIMEは100ns単位
    }

    // スレッド数はToolhelpスナップショット（CreateToolhelp32Snapshot）で数える。
    // Process.GetProcesses()系とは別の列挙APIを使う（ProcessMonitorHandler参照）。
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

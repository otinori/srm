using System.ComponentModel;
using System.Runtime.InteropServices;
using Srm.Runtime.Native;

namespace Srm.Runtime;

public sealed class JobObjectManager : IDisposable
{
    private IntPtr _hJob = IntPtr.Zero;
    private bool _disposed;

    // 名前を付けるのはWindowGuard（DC-017）が別プロセス（Srm.Mcp）から
    // OpenJobObjectで再オープンし、PID所属を確認できるようにするため。
    // 呼び出し元がnullを渡した場合は無名のまま（既存のTier1呼び出し以外の用途向け）。
    public static JobObjectManager Create(int maxProcesses, string? name = null)
    {
        var mgr = new JobObjectManager();
        mgr._hJob = JobObjectNative.CreateJobObject(IntPtr.Zero, name);
        if (mgr._hJob == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Job Objectの作成に失敗しました");

        // srm runはサンドボックス対象のアプリを起動した後すぐに終了し、アプリ自体は
        // バックグラウンドで動き続ける（srm stop で別途停止する）設計のため、
        // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSEは付けない。これを付けるとsrm.exeが
        // 終了してJob Objectのハンドルを閉じた瞬間に起動直後のアプリも強制終了されて
        // しまう。プロセス数の上限はJOB_OBJECT_LIMIT_ACTIVE_PROCESSのみで、ハンドルが
        // 閉じてもメンバープロセスが生きている限りJob Object自体は維持される
        // （名前を付けているため、後から別プロセスがOpenJobObjectで再アクセスできる）。
        var info = new JobObjectNative.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JobObjectNative.JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JobObjectNative.JOB_OBJECT_LIMIT_ACTIVE_PROCESS,
                ActiveProcessLimit = (uint)maxProcesses,
            }
        };

        var size = (uint)Marshal.SizeOf(info);
        var ptr = Marshal.AllocHGlobal((int)size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!JobObjectNative.SetInformationJobObject(
                    mgr._hJob,
                    JobObjectNative.JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
                    ptr,
                    size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Job Object設定に失敗しました");
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }

        return mgr;
    }

    public void AssignProcess(IntPtr hProcess)
    {
        if (!JobObjectNative.AssignProcessToJobObject(_hJob, hProcess))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "プロセスをJob Objectに割り当てられませんでした");
    }

    // WindowGuard（DC-017）専用: 名前付きJob Objectを別プロセスから再オープンし、
    // 現在のメンバープロセスのPID一覧を取得する。Job Objectが既に存在しない
    // （対象アプリが終了しJob自体が破棄された）場合はfalseを返す。
    public static bool TryGetProcessIds(string jobName, out int[] processIds)
    {
        processIds = Array.Empty<int>();

        var hJob = JobObjectNative.OpenJobObject(JobObjectNative.JOB_OBJECT_QUERY, false, jobName);
        if (hJob == IntPtr.Zero) return false;

        try
        {
            // JOBOBJECT_BASIC_PROCESS_ID_LIST は末尾に可変長のPID配列を持つため、
            // 十分な件数を確保できるバッファを確保して手動でパースする。
            const int maxPids = 256;
            var size = (uint)(sizeof(uint) * 2 + IntPtr.Size * maxPids);
            var buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (!JobObjectNative.QueryInformationJobObject(
                        hJob,
                        JobObjectNative.JOBOBJECTINFOCLASS.JobObjectBasicProcessIdList,
                        buf,
                        size,
                        out _))
                    return false;

                var numberOfProcessIdsInList = (uint)Marshal.ReadInt32(buf, 4);
                var count = (int)Math.Min(numberOfProcessIdsInList, maxPids);
                var ids = new int[count];
                for (var i = 0; i < count; i++)
                {
                    var pidPtr = Marshal.ReadIntPtr(buf, 8 + i * IntPtr.Size);
                    ids[i] = (int)pidPtr;
                }
                processIds = ids;
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        finally
        {
            JobObjectNative.CloseHandle(hJob);
        }
    }

    public void Dispose()
    {
        if (!_disposed && _hJob != IntPtr.Zero)
        {
            JobObjectNative.CloseHandle(_hJob);
            _hJob = IntPtr.Zero;
        }
        _disposed = true;
    }
}

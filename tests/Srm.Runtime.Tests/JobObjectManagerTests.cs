using System.Diagnostics;
using Xunit;

namespace Srm.Runtime.Tests;

// WindowGuard（Srm.Mcp）が別プロセスからOpenJobObjectで再オープンしてPID所属を
// 確認できることを、実際に名前付きJob Objectを作成・プロセスを割り当てて確認する。
// Windows実機（CIのwindows-latestランナーを含む）でのみ実行される。
public class JobObjectManagerTests
{
    [WindowsOnlyFact]
    public void NamedJobObject_ReopenedByName_ReturnsAssignedProcessId()
    {
        var jobName = $"srm-test-job-{Guid.NewGuid():N}";
        using var job = JobObjectManager.Create(maxProcesses: 5, jobName);

        var process = Process.Start("notepad.exe")!;
        try
        {
            job.AssignProcess(process.Handle);

            var found = JobObjectManager.TryGetProcessIds(jobName, out var pids);

            Assert.True(found);
            Assert.Contains(process.Id, pids);
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 既に終了済みの場合は無視 */ }
        }
    }

    [WindowsOnlyFact]
    public void TryGetProcessIds_UnknownJobName_ReturnsFalse()
    {
        var found = JobObjectManager.TryGetProcessIds($"srm-nonexistent-{Guid.NewGuid():N}", out var pids);

        Assert.False(found);
        Assert.Empty(pids);
    }
}

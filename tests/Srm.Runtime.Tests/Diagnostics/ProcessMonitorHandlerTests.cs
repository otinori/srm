using System.Diagnostics;
using Srm.Runtime.Diagnostics;
using Xunit;

namespace Srm.Runtime.Tests.Diagnostics;

public class ProcessMonitorHandlerTests
{
    private readonly ProcessMonitorHandler _sut = new();

    [Fact]
    public void Handle_CurrentProcess_ReturnsSnapshot()
    {
        var currentPid = Environment.ProcessId;

        var result = _sut.Handle("req-1", new[] { currentPid });

        Assert.True(result.Success);
        Assert.Equal("req-1", result.RequestId);
        var snapshot = Assert.Single(result.Processes);
        Assert.Equal(currentPid, snapshot.ProcessId);
        Assert.True(snapshot.ThreadCount > 0);
    }

    [Fact]
    public void Handle_AllPidsUnresolvable_ReturnsFailure()
    {
        var result = _sut.Handle("req-1", new[] { FindUnusedProcessId() });

        Assert.False(result.Success);
        Assert.NotNull(result.Reason);
        Assert.Empty(result.Processes);
    }

    [Fact]
    public void Handle_MixOfResolvableAndUnresolvablePids_SkipsUnresolvableOnly()
    {
        var currentPid = Environment.ProcessId;

        var result = _sut.Handle("req-1", new[] { currentPid, FindUnusedProcessId() });

        Assert.True(result.Success);
        var snapshot = Assert.Single(result.Processes);
        Assert.Equal(currentPid, snapshot.ProcessId);
    }

    private static int FindUnusedProcessId()
    {
        for (var candidate = 999_999; candidate > 100_000; candidate--)
        {
            try { Process.GetProcessById(candidate); }
            catch (ArgumentException) { return candidate; }
        }
        throw new InvalidOperationException("未使用PIDが見つかりませんでした");
    }
}

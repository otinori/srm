using System.Diagnostics;
using Srm.Runtime.Diagnostics;
using Srm.Runtime.Sandbox;
using Xunit;

namespace Srm.Runtime.Tests;

// DC-016（claude-code CLIをAppContainer内で-p実行するとカーネルモードCPUを消費し
// 続ける未解決バグ）の実機再調査を支える診断サンプラーの単体テスト。
// GetProcessMemoryInfo（psapi.dll）等の実Win32呼び出しを伴うため、
// FocusRequestHandlerTestsと同様Windows実機（CIのwindows-latestランナーを含む）
// でのみ実行される。
public class DiagCollectorTests : IDisposable
{
    private Process? _target;

    public void Dispose()
    {
        try { _target?.Kill(entireProcessTree: true); } catch { /* 既に終了済みの場合は無視 */ }
    }

    [WindowsOnlyFact]
    public void Handle_RunningProcess_ReturnsPlausibleSample()
    {
        _target = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true })!;
        _target.WaitForInputIdle(5000);

        var result = new DiagCollector().Handle(
            _target.Id, new DiagRequestModel { RequestId = "req-1", SampleWindowMs = 300 });

        Assert.True(result.Success);
        Assert.Equal("req-1", result.RequestId);
        Assert.Equal(_target.Id, result.ProcessId);
        Assert.True(result.SampleWindowMs >= 300);
        Assert.True(result.UserTimePercent >= 0);
        Assert.True(result.KernelTimePercent >= 0);
        Assert.True(result.PageFaultsPerSec >= 0);
        Assert.True(result.ThreadCount > 0);
        Assert.True(result.HandleCount > 0);
    }

    [WindowsOnlyFact]
    public void Handle_UnknownProcessId_ReturnsFailureWithoutThrowing()
    {
        var unlikelyPid = FindUnusedPid();

        var result = new DiagCollector().Handle(
            unlikelyPid, new DiagRequestModel { RequestId = "req-1", SampleWindowMs = 100 });

        Assert.False(result.Success);
        Assert.NotNull(result.Reason);
        Assert.Equal("req-1", result.RequestId);
    }

    private static int FindUnusedPid()
    {
        for (var candidate = 999_999; candidate > 0; candidate--)
        {
            try { Process.GetProcessById(candidate); }
            catch (ArgumentException) { return candidate; }
        }

        throw new InvalidOperationException("未使用のPIDが見つかりませんでした");
    }
}

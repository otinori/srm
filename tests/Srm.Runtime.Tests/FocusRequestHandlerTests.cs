using System.Diagnostics;
using Srm.Runtime.Interaction;
using Srm.Runtime.Sandbox;
using Xunit;

namespace Srm.Runtime.Tests;

// notepad.exeを実際に起動してウィンドウを取得し、FocusRequestHandlerの本体ロジック
// （tier2-channel-a-focus-guarantee）を実Win32 API呼び出しで検証する。
// WindowGuardTestsと同様、Windows実機（CIのwindows-latestランナーを含む）でのみ
// 実行される。
public class FocusRequestHandlerTests : IDisposable
{
    private Process? _notepad;

    public void Dispose()
    {
        try { _notepad?.Kill(entireProcessTree: true); } catch { /* 既に終了済みの場合は無視 */ }
    }

    private int StartNotepad()
    {
        _notepad = Process.Start("notepad.exe")!;
        _notepad.WaitForInputIdle(5000);

        for (var i = 0; i < 50 && _notepad.MainWindowHandle == IntPtr.Zero; i++)
        {
            Thread.Sleep(100);
            _notepad.Refresh();
        }

        return _notepad.Id;
    }

    [WindowsOnlyFact]
    public void Handle_AppNameMismatch_ReturnsFailureWithoutTouchingWindow()
    {
        var pid = StartNotepad();

        var result = new FocusRequestHandler().Handle(
            "notepad-test", new HashSet<int> { pid },
            new FocusRequestModel { RequestId = "req-1", App = "different-app" });

        Assert.False(result.Success);
        Assert.Equal("req-1", result.RequestId);
        Assert.NotNull(result.Reason);
    }

    [WindowsOnlyFact]
    public void Handle_NoCandidateWindow_ReturnsFailure()
    {
        var result = new FocusRequestHandler().Handle(
            "notepad-test", new HashSet<int> { -1 },
            new FocusRequestModel { RequestId = "req-1", App = "notepad-test" });

        Assert.False(result.Success);
        Assert.NotNull(result.Reason);
    }

    [WindowsOnlyFact]
    public void Handle_ValidRequest_FocusesWindowAndReturnsSuccess()
    {
        var pid = StartNotepad();

        var result = new FocusRequestHandler().Handle(
            "notepad-test", new HashSet<int> { pid },
            new FocusRequestModel { RequestId = "req-1", App = "notepad-test" });

        Assert.True(result.Success, result.Reason);
        Assert.Equal("req-1", result.RequestId);
    }
}

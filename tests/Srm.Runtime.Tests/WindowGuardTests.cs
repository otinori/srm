using System.Diagnostics;
using Srm.Runtime.Interaction;
using Xunit;

namespace Srm.Runtime.Tests;

// notepad.exeを実際に起動してウィンドウを取得し、WindowGuardの各チェックを実Win32 API
// 呼び出しで検証する。Windows実機（CIのwindows-latestランナーを含む）でのみ実行され、
// Linux開発環境ではSkippedとして報告される（WindowsOnlyFactAttribute参照）。
public class WindowGuardTests : IDisposable
{
    private Process? _notepad;

    public void Dispose()
    {
        try { _notepad?.Kill(entireProcessTree: true); } catch { /* 既に終了済みの場合は無視 */ }
    }

    private IntPtr StartNotepadAndGetWindow()
    {
        _notepad = Process.Start("notepad.exe")!;
        _notepad.WaitForInputIdle(5000);

        for (var i = 0; i < 50 && _notepad.MainWindowHandle == IntPtr.Zero; i++)
        {
            Thread.Sleep(100);
            _notepad.Refresh();
        }

        return _notepad.MainWindowHandle;
    }

    [WindowsOnlyFact]
    public void Authorize_OwnerPidInAllowedSet_Allows()
    {
        var hwnd = StartNotepadAndGetWindow();
        Assert.NotEqual(IntPtr.Zero, hwnd);

        var result = new WindowGuard().Authorize(new WindowGuardRequest(
            hwnd, new HashSet<int> { _notepad!.Id }, RequireForeground: false));

        Assert.True(result.Allowed, result.Reason);
    }

    [WindowsOnlyFact]
    public void Authorize_OwnerPidNotInAllowedSet_Denies()
    {
        var hwnd = StartNotepadAndGetWindow();

        var result = new WindowGuard().Authorize(new WindowGuardRequest(
            hwnd, new HashSet<int> { -1 }, RequireForeground: false));

        Assert.False(result.Allowed);
        Assert.NotNull(result.Reason);
    }

    [WindowsOnlyFact]
    public void Authorize_UnexpectedClassName_Denies()
    {
        var hwnd = StartNotepadAndGetWindow();

        var result = new WindowGuard().Authorize(new WindowGuardRequest(
            hwnd, new HashSet<int> { _notepad!.Id },
            ExpectedClassName: "ThisClassNameDoesNotExist",
            RequireForeground: false));

        Assert.False(result.Allowed);
    }

    [WindowsOnlyFact]
    public void Authorize_UnexpectedExecutablePath_Denies()
    {
        var hwnd = StartNotepadAndGetWindow();

        var result = new WindowGuard().Authorize(new WindowGuardRequest(
            hwnd, new HashSet<int> { _notepad!.Id },
            ExpectedExecutablePath: @"C:\definitely\not\notepad.exe",
            RequireForeground: false));

        Assert.False(result.Allowed);
    }

    [WindowsOnlyFact]
    public void Authorize_UnexpectedExecutableDirectory_Denies()
    {
        var hwnd = StartNotepadAndGetWindow();

        var result = new WindowGuard().Authorize(new WindowGuardRequest(
            hwnd, new HashSet<int> { _notepad!.Id },
            ExpectedExecutableDirectory: @"C:\definitely\not\a\real\dir",
            RequireForeground: false));

        Assert.False(result.Allowed);
    }

    [WindowsOnlyFact]
    public void Authorize_ExecutableNameDoesNotContainExpected_Denies()
    {
        var hwnd = StartNotepadAndGetWindow();

        var result = new WindowGuard().Authorize(new WindowGuardRequest(
            hwnd, new HashSet<int> { _notepad!.Id },
            ExpectedExecutableNameContains: "ThisStringWontMatch",
            RequireForeground: false));

        Assert.False(result.Allowed);
    }

    [WindowsOnlyFact]
    public void Authorize_MousePointFarOutsideWindow_Denies()
    {
        var hwnd = StartNotepadAndGetWindow();

        var result = new WindowGuard().Authorize(new WindowGuardRequest(
            hwnd, new HashSet<int> { _notepad!.Id },
            ScreenPointForMouse: (-10000, -10000),
            RequireForeground: false));

        Assert.False(result.Allowed);
    }
}

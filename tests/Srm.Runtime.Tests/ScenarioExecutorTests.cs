using System.Diagnostics;
using Srm.Runtime.Sandbox;
using Xunit;

namespace Srm.Runtime.Tests;

// notepad.exeを実際に起動し、ScenarioExecutorがsend_key/screenshotステップを実行
// できることを確認する。Windows実機（CIのwindows-latestランナーを含む）でのみ
// 実行される（WindowsOnlyFactAttribute参照）。
public class ScenarioExecutorTests : IDisposable
{
    private readonly string _outboxDir = Path.Combine(Path.GetTempPath(), "srm-scenario-outbox-" + Guid.NewGuid());
    private Process? _notepad;

    public void Dispose()
    {
        try { _notepad?.Kill(entireProcessTree: true); } catch { /* 既に終了済みの場合は無視 */ }
        if (Directory.Exists(_outboxDir)) Directory.Delete(_outboxDir, true);
    }

    private int StartNotepadAndWaitForWindow()
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
    public void Execute_SendKeyThenScreenshot_SucceedsAndWritesArtifact()
    {
        var pid = StartNotepadAndWaitForWindow();
        var allowedPids = new HashSet<int> { pid };

        var scenario = new ScenarioModel
        {
            Steps =
            [
                new ScenarioStep { Type = "send_key", Text = "hello from ScenarioExecutorTests" },
                new ScenarioStep { Type = "screenshot", Label = "after-typing" },
            ],
        };

        var result = new ScenarioExecutor().Execute(scenario, allowedPids, _outboxDir);

        Assert.True(result.Success, string.Join("; ", result.Steps.Select(s => s.Error)));
        Assert.Equal(2, result.Steps.Count);
        Assert.True(result.Steps[0].Success);
        Assert.True(result.Steps[1].Success);
        Assert.NotNull(result.Steps[1].ArtifactRelativePath);
        Assert.True(File.Exists(Path.Combine(_outboxDir, result.Steps[1].ArtifactRelativePath!)));
    }

    [WindowsOnlyFact]
    public void Execute_DisallowedPid_FailsFirstStep()
    {
        StartNotepadAndWaitForWindow();
        var allowedPids = new HashSet<int> { -1 }; // notepadのPIDを含めない

        var scenario = new ScenarioModel
        {
            Steps = [new ScenarioStep { Type = "send_key", Text = "should not be sent" }],
        };

        var result = new ScenarioExecutor().Execute(scenario, allowedPids, _outboxDir);

        Assert.False(result.Success);
        Assert.Single(result.Steps);
        Assert.False(result.Steps[0].Success);
    }
}

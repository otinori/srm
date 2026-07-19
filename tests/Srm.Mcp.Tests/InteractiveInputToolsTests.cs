using ModelContextProtocol;
using Srm.Mcp.Tools;
using Srm.Runtime;
using Srm.Runtime.Sandbox;
using Xunit;

namespace Srm.Mcp.Tests;

// tier2-channel-a-focus-guarantee: EnsureGuestFocusはTier1では即座に何もせず
// 返るべきで、Tier2ではfocus-request.jsonを書いてfocus-result.jsonを待つべき。
// 実際のWin32ウィンドウ操作を伴わない（純粋なファイルIOの）ため、
// SandboxControlChannelTestsと同様Windows/Linux両方で実行できる。
public class InteractiveInputToolsTests : IDisposable
{
    private readonly string _controlDir = Path.Combine(Path.GetTempPath(), "srm-focus-" + Guid.NewGuid());
    private readonly InteractiveInputTools _sut = new();

    public InteractiveInputToolsTests() => Directory.CreateDirectory(_controlDir);

    public void Dispose() { if (Directory.Exists(_controlDir)) Directory.Delete(_controlDir, true); }

    [Fact]
    public void EnsureGuestFocus_Tier1_ReturnsImmediatelyWithoutTouchingControlDir()
    {
        var app = new RunningApp { Name = "tier1-app", Tier = 1, Pid = 1, StartedAt = DateTimeOffset.Now, PolicyPath = "x", ControlDir = null };

        // ControlDirがnullでも例外にならないこと（Tier1経路には一切踏み込まない）
        var ex = Record.Exception(() => _sut.EnsureGuestFocus(app, TimeSpan.FromMilliseconds(100)));
        Assert.Null(ex);
    }

    [Fact]
    public void EnsureGuestFocus_Tier2_TimesOutWhenGuestNeverResponds()
    {
        var app = new RunningApp { Name = "tier2-app", Tier = 2, Pid = 1, StartedAt = DateTimeOffset.Now, PolicyPath = "x", ControlDir = _controlDir };

        var ex = Assert.Throws<McpException>(() => _sut.EnsureGuestFocus(app, TimeSpan.FromMilliseconds(300)));
        Assert.Contains("タイムアウト", ex.Message);
    }

    [Fact]
    public void EnsureGuestFocus_Tier2_ThrowsWhenGuestReportsFailure()
    {
        var app = new RunningApp { Name = "tier2-app", Tier = 2, Pid = 1, StartedAt = DateTimeOffset.Now, PolicyPath = "x", ControlDir = _controlDir };
        var control = new SandboxControlChannel(_controlDir);

        var t = new Thread(() =>
        {
            // ゲスト側がリクエストを検出して失敗結果を書く挙動を模擬する。
            FocusRequestModel? request = null;
            for (var i = 0; i < 50 && request == null; i++)
            {
                Thread.Sleep(50);
                request = control.TryReadFocusRequest();
            }
            Assert.NotNull(request);
            control.WriteFocusResult(new FocusResultModel { RequestId = request!.RequestId, Success = false, Reason = "対象ウィンドウが見つかりません" });
        });
        t.Start();

        var ex = Assert.Throws<McpException>(() => _sut.EnsureGuestFocus(app, TimeSpan.FromSeconds(2)));
        Assert.Contains("対象ウィンドウが見つかりません", ex.Message);
        t.Join();
    }

    [Fact]
    public void EnsureGuestFocus_Tier2_SucceedsWhenGuestConfirmsFocus()
    {
        var app = new RunningApp { Name = "tier2-app", Tier = 2, Pid = 1, StartedAt = DateTimeOffset.Now, PolicyPath = "x", ControlDir = _controlDir };
        var control = new SandboxControlChannel(_controlDir);

        var t = new Thread(() =>
        {
            FocusRequestModel? request = null;
            for (var i = 0; i < 50 && request == null; i++)
            {
                Thread.Sleep(50);
                request = control.TryReadFocusRequest();
            }
            Assert.NotNull(request);
            Assert.Equal(app.Name, request!.App);
            control.WriteFocusResult(new FocusResultModel { RequestId = request.RequestId, Success = true });
        });
        t.Start();

        var ex = Record.Exception(() => _sut.EnsureGuestFocus(app, TimeSpan.FromSeconds(2)));
        Assert.Null(ex);
        t.Join();
    }
}

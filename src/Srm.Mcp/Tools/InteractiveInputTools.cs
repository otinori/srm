using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Srm.Runtime;
using Srm.Runtime.Interaction;
using Srm.Runtime.Logging;
using Srm.Runtime.Sandbox;

namespace Srm.Mcp.Tools;

// チャネルA（対話的ライブ制御、DC-017）。Tier1は対象プロセス自身のウィンドウへ
// 直接、Tier2はWindowsSandbox.exeがホスト上に開くRDPレンダリングウィンドウへ
// SendInputする（ゲスト内`--nested`側の変更は不要）。
[McpServerToolType]
public class InteractiveInputTools
{
    private static readonly TimeSpan FocusRequestTimeout = TimeSpan.FromSeconds(2);

    private readonly Tier1WindowResolver _tier1Resolver = new();
    private readonly Tier2SandboxWindowResolver _tier2Resolver = new();
    private readonly WindowGuard _guard = new();

    [McpServerTool(Name = "send_key"), Description(
        "SRMが管理しているアプリのウィンドウへキー入力（テキスト）を送る。対象はrunning appのウィンドウに限定され、" +
        "毎回ウィンドウの実体を再検証してから送信する（対象外の場合はエラーになる）。" +
        "Tier2の場合、送信前にゲスト内で対象アプリへ確実にフォーカスを設定してから送信する" +
        "（tier2-channel-a-focus-guarantee）。ゲスト側でフォーカス設定に失敗、またはタイムアウトした場合はエラーになる。")]
    public string SendKey(
        [Description("送信先のアプリ名（srm_listに表示される名前）")] string app,
        [Description("入力するテキスト（Unicode文字としてそのまま送信される。Enter等の特殊キーは未対応）")] string text)
    {
        var (runningApp, hwnd) = ResolveTargetWindow(app);
        EnsureGuestFocus(runningApp);

        var guardResult = _guard.Authorize(BuildRequest(runningApp, hwnd));

        if (!guardResult.Allowed)
        {
            LogGuardRejected(app, "send_key", guardResult.Reason!);
            throw new McpException($"send_keyを拒否しました: {guardResult.Reason}");
        }

        InputSender.SendUnicodeText(text);
        new StructuredLogger(app).Info("send_key", new { text_length = text.Length });
        return $"{app} へ{text.Length}文字送信しました";
    }

    [McpServerTool(Name = "send_mouse"), Description(
        "SRMが管理しているアプリのウィンドウへマウスクリックを送る。座標は対象ウィンドウのクライアント領域内の相対座標で指定する。" +
        "Tier2の場合、送信前にゲスト内で対象アプリへ確実にフォーカスを設定してから送信する" +
        "（tier2-channel-a-focus-guarantee）。ゲスト側でフォーカス設定に失敗、またはタイムアウトした場合はエラーになる。")]
    public string SendMouse(
        [Description("送信先のアプリ名（srm_listに表示される名前）")] string app,
        [Description("クライアント領域内のX座標")] int x,
        [Description("クライアント領域内のY座標")] int y)
    {
        var (runningApp, hwnd) = ResolveTargetWindow(app);
        EnsureGuestFocus(runningApp);

        var screenPoint = WindowCoordinates.ClientToScreen(hwnd, x, y);

        var guardResult = _guard.Authorize(BuildRequest(runningApp, hwnd, screenPoint));

        if (!guardResult.Allowed)
        {
            LogGuardRejected(app, "send_mouse", guardResult.Reason!);
            throw new McpException($"send_mouseを拒否しました: {guardResult.Reason}");
        }

        InputSender.SendLeftClick(screenPoint.X, screenPoint.Y);
        new StructuredLogger(app).Info("send_mouse", new { x, y });
        return $"{app} の({x},{y})をクリックしました";
    }

    [McpServerTool(Name = "screenshot"), Description(
        "SRMが管理しているアプリのウィンドウをキャプチャしてPNG画像を返す（Tier2の場合はゲストデスクトップ全体が写る）。" +
        "画像はウィンドウ全体（タイトルバー・メニューバー等を含む）だが、send_mouseの座標系はクライアント領域基準のため" +
        "原点が一致しない。画像と併せて返るclient_offsetを画像上のピクセル座標から差し引いた値をsend_mouseに渡すこと。")]
    public CallToolResult Screenshot([Description("対象のアプリ名（srm_listに表示される名前）")] string app)
    {
        var (runningApp, hwnd) = ResolveTargetWindow(app);

        var guardResult = _guard.Authorize(BuildRequest(runningApp, hwnd, requireForeground: false));

        if (!guardResult.Allowed)
        {
            LogGuardRejected(app, "screenshot", guardResult.Reason!);
            throw new McpException($"screenshotを拒否しました: {guardResult.Reason}");
        }

        var png = WindowCapture.CapturePng(hwnd);
        var clientOffset = WindowCoordinates.GetClientOffsetWithinWindow(hwnd);
        new StructuredLogger(app).Info("screenshot", new { bytes = png.Length });

        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = $"client_offset: x={clientOffset.X}, y={clientOffset.Y}" +
                        "（画像上のこの位置がクライアント領域の原点=send_mouseの(0,0)に対応する。" +
                        "画像を見て決めたピクセル座標からこのoffsetを引いてからsend_mouseへ渡すこと）",
                },
                ImageContentBlock.FromBytes(png, "image/png"),
            ],
        };
    }

    private (RunningApp App, IntPtr Hwnd) ResolveTargetWindow(string app)
    {
        var runningApp = RunningAppRegistry.Find(app)
            ?? throw new McpException($"実行中のアプリが見つかりません: {app}");

        var (allowedPids, candidates) = runningApp.Tier == 2
            ? ResolveTier2(runningApp)
            : ResolveTier1(runningApp);

        if (candidates.Count == 0)
            throw new McpException($"{app} の対象ウィンドウが見つかりません（起動直後でまだウィンドウが無い可能性があります）");

        // 最も手前（EnumWindowsのZオーダーで先頭）のウィンドウを対象にする。
        // 複数ウィンドウを持つアプリの特定ウィンドウを狙い撃つ機能は今のところ無い。
        return (runningApp, candidates[0]);
    }

    private (HashSet<int> AllowedPids, List<IntPtr> Candidates) ResolveTier1(RunningApp app)
    {
        var allowedPids = _tier1Resolver.GetAllowedPids(app);
        return (allowedPids, _tier1Resolver.ResolveCandidateWindows(allowedPids));
    }

    private (HashSet<int> AllowedPids, List<IntPtr> Candidates) ResolveTier2(RunningApp app)
    {
        var allowedPids = _tier2Resolver.GetAllowedPids(app);
        return (allowedPids, _tier2Resolver.ResolveCandidateWindows(allowedPids));
    }

    private WindowGuardRequest BuildRequest(
        RunningApp app, IntPtr hwnd, (int X, int Y)? screenPointForMouse = null, bool requireForeground = true)
    {
        if (app.Tier == 2)
        {
            return new WindowGuardRequest(
                hwnd,
                _tier2Resolver.GetAllowedPids(app),
                ExpectedExecutableDirectory: Tier2SandboxWindowResolver.ExpectedDirectory,
                ExpectedExecutableNameContains: Tier2SandboxWindowResolver.ExpectedExecutableNameContains,
                ScreenPointForMouse: screenPointForMouse,
                RequireForeground: requireForeground);
        }

        return new WindowGuardRequest(
            hwnd,
            _tier1Resolver.GetAllowedPids(app),
            ScreenPointForMouse: screenPointForMouse,
            RequireForeground: requireForeground);
    }

    // tier2-channel-a-focus-guarantee: Tier2の場合のみ、実際のSendInputの前に
    // ゲスト内`--nested`（同一セッション）へフォーカス要求を出し、対象アプリが
    // 実際に入力フォーカスを持ったことを確認する。ホスト側WindowGuardの
    // フォアグラウンド検証は`WindowsSandboxClient.exe`ウィンドウ（RDP描画面）に
    // ついてのみ行われゲスト内部までは検証できないため、この一段が無いと
    // guardが許可してもゲスト内で意図しない場所に入力が届くことがある
    // （DC-017 9.3実機検証で確認済み）。Tier1はこの経路を通らず、従来通り
    // 追加の待機なしに動作する。
    private void EnsureGuestFocus(RunningApp app) => EnsureGuestFocus(app, FocusRequestTimeout);

    // タイムアウトを引数化しているのはテスト用（既定の2秒を待たずに
    // タイムアウト経路を検証できるようにするため）。
    internal void EnsureGuestFocus(RunningApp app, TimeSpan timeout)
    {
        if (app.Tier != 2) return;

        var control = new SandboxControlChannel(app.ControlDir!);
        var requestId = Guid.NewGuid().ToString();
        control.WriteFocusRequest(new FocusRequestModel { RequestId = requestId, App = app.Name });

        var result = control.WaitForFocusResult(requestId, timeout);
        if (result == null)
            throw new McpException($"ゲスト内でのフォーカス確認がタイムアウトしました: {app.Name}");
        if (!result.Success)
            throw new McpException($"ゲスト内でのフォーカス設定に失敗しました: {result.Reason}");
    }

    private static void LogGuardRejected(string app, string tool, string reason) =>
        new StructuredLogger(app).Warn("guard_rejected", new { tool, reason });
}

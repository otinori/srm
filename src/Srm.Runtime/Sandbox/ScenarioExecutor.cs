using Srm.Runtime.Interaction;

namespace Srm.Runtime.Sandbox;

// チャネルB（オートパイロット、DC-017）: ゲスト内`--nested`から呼ばれる。対象アプリと
// `--nested`自身は同一セッション上で動くため、Tier1の対話的ライブ制御（チャネルA）と
// 全く同じ「同一セッション内SendInput + WindowGuard」ロジックをそのまま再利用する
// （ゲスト内専用のウィンドウ解決トリックは不要）。
public class ScenarioExecutor
{
    private readonly WindowGuard _guard = new();
    private readonly Tier1WindowResolver _windowResolver = new();

    // 最初に失敗したステップで打ち切る（それ以降のステップは前提が崩れている
    // 可能性が高いため、無意味な操作を続けない）。
    public ScenarioResultModel Execute(ScenarioModel scenario, HashSet<int> allowedPids, string outboxDir)
    {
        var stepResults = new List<ScenarioStepResult>();
        var overallSuccess = true;

        for (var i = 0; i < scenario.Steps.Count; i++)
        {
            var stepResult = ExecuteStep(i, scenario.Steps[i], allowedPids, outboxDir);
            stepResults.Add(stepResult);
            if (!stepResult.Success)
            {
                overallSuccess = false;
                break;
            }
        }

        return new ScenarioResultModel
        {
            Success = overallSuccess,
            Steps = stepResults,
            CompletedAt = DateTimeOffset.Now,
        };
    }

    private ScenarioStepResult ExecuteStep(int index, ScenarioStep step, HashSet<int> allowedPids, string outboxDir)
    {
        try
        {
            var candidates = _windowResolver.ResolveCandidateWindows(allowedPids);
            if (candidates.Count == 0)
                return Fail(index, step.Type, "対象ウィンドウが見つかりません");

            var hwnd = candidates[0];

            return step.Type switch
            {
                "send_key" => ExecuteSendKey(index, step, hwnd, allowedPids),
                "send_mouse" => ExecuteSendMouse(index, step, hwnd, allowedPids),
                "screenshot" => ExecuteScreenshot(index, step, hwnd, allowedPids, outboxDir),
                _ => Fail(index, step.Type, $"未知のステップ種別です: {step.Type}"),
            };
        }
        catch (Exception ex)
        {
            return Fail(index, step.Type, ex.Message);
        }
    }

    private ScenarioStepResult ExecuteSendKey(int index, ScenarioStep step, IntPtr hwnd, HashSet<int> allowedPids)
    {
        var guardResult = _guard.Authorize(new WindowGuardRequest(hwnd, allowedPids));
        if (!guardResult.Allowed) return Fail(index, step.Type, guardResult.Reason!);

        InputSender.SendUnicodeText(step.Text ?? "");
        return Ok(index, step.Type);
    }

    private ScenarioStepResult ExecuteSendMouse(int index, ScenarioStep step, IntPtr hwnd, HashSet<int> allowedPids)
    {
        var screenPoint = WindowCoordinates.ClientToScreen(hwnd, step.X ?? 0, step.Y ?? 0);
        var guardResult = _guard.Authorize(new WindowGuardRequest(hwnd, allowedPids, ScreenPointForMouse: screenPoint));
        if (!guardResult.Allowed) return Fail(index, step.Type, guardResult.Reason!);

        InputSender.SendLeftClick(screenPoint.X, screenPoint.Y);
        return Ok(index, step.Type);
    }

    private ScenarioStepResult ExecuteScreenshot(int index, ScenarioStep step, IntPtr hwnd, HashSet<int> allowedPids, string outboxDir)
    {
        var guardResult = _guard.Authorize(new WindowGuardRequest(hwnd, allowedPids, RequireForeground: false));
        if (!guardResult.Allowed) return Fail(index, step.Type, guardResult.Reason!);

        var png = WindowCapture.CapturePng(hwnd);
        var label = string.IsNullOrWhiteSpace(step.Label) ? "screenshot" : step.Label;
        var fileName = $"scenario-step-{index:D3}-{label}.png";

        Directory.CreateDirectory(outboxDir);
        File.WriteAllBytes(Path.Combine(outboxDir, fileName), png);

        return Ok(index, step.Type, fileName);
    }

    private static ScenarioStepResult Ok(int index, string type, string? artifact = null) =>
        new() { Index = index, Type = type, Success = true, ArtifactRelativePath = artifact };

    private static ScenarioStepResult Fail(int index, string type, string error) =>
        new() { Index = index, Type = type, Success = false, Error = error };
}

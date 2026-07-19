using Srm.Runtime.Sandbox;

namespace Srm.Runtime.Interaction;

// tier2-channel-a-focus-guarantee: ゲスト内`--nested`がfocus-request.jsonを検出した
// ときに呼ぶ、直接テスト可能な本体ロジック。RunCommand.HandleFocusRequestは
// allowedPidsの算出とcontrol.WriteFocusResultへの書き込みだけを担う薄いラッパーで、
// ScenarioExecutorと同じ「本体は独立クラスとして実Win32テストできるようにする」
// パターンを踏襲する。
public class FocusRequestHandler
{
    public FocusResultModel Handle(string policyName, HashSet<int> allowedPids, FocusRequestModel request)
    {
        if (request.App != policyName)
            return new FocusResultModel
            {
                RequestId = request.RequestId,
                Success = false,
                Reason = $"このゲストで実行中のアプリ（{policyName}）と要求されたアプリ（{request.App}）が一致しません",
            };

        var candidates = new Tier1WindowResolver().ResolveCandidateWindows(allowedPids);
        if (candidates.Count == 0)
            return new FocusResultModel
            {
                RequestId = request.RequestId,
                Success = false,
                Reason = $"{request.App} の対象ウィンドウが見つかりません",
            };

        var guardResult = new WindowGuard().Authorize(new WindowGuardRequest(candidates[0], allowedPids));
        return new FocusResultModel
        {
            RequestId = request.RequestId,
            Success = guardResult.Allowed,
            Reason = guardResult.Reason,
        };
    }
}

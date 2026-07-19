using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Srm.Runtime;
using Srm.Runtime.Logging;
using Srm.Runtime.Sandbox;

namespace Srm.Mcp.Tools;

// チャネルB（オートパイロット、Tier2専用、DC-017）。手順の並び（シナリオ）を
// まとめて1回投入し、ゲスト内`--nested`が自律的に最後まで実行する。host↔guest間の
// 常時ラウンドトリップに依存しないため、通信が一時的に不安定でも投入後は完走できる。
[McpServerToolType]
public class AutopilotTools
{
    [McpServerTool(Name = "run_scenario"), Description(
        "Tier2アプリに対してテストシナリオ（手順の並び）を投入し、ゲスト内で自律実行させる。" +
        "対象appはsrm_runで起動済み（Tier2）である必要がある。各ステップのtypeは" +
        "send_key(text)/send_mouse(x,y)/screenshot(label任意)のいずれか。投入は非同期で、" +
        "結果はget_scenario_resultで別途確認する。")]
    public string RunScenario(
        [Description("対象のTier2アプリ名（srm_listに表示される名前）")] string app,
        [Description("実行する手順の並び")] List<ScenarioStep> steps)
    {
        var runningApp = RunningAppRegistry.Find(app)
            ?? throw new McpException($"実行中のアプリが見つかりません: {app}");

        if (runningApp.Tier != 2)
            throw new McpException($"{app} はTier{runningApp.Tier}です。run_scenarioはTier2専用です。");

        if (string.IsNullOrWhiteSpace(runningApp.ControlDir))
            throw new McpException($"{app} のcontrolディレクトリが記録されていません。");

        if (steps.Count == 0)
            throw new McpException("steps は1件以上指定してください。");

        var scenario = new ScenarioModel { Steps = steps };
        new SandboxControlChannel(runningApp.ControlDir).WriteScenario(scenario);
        new StructuredLogger(app).Info("run_scenario", new { step_count = steps.Count, step_types = steps.Select(s => s.Type) });

        return $"{app} にシナリオを投入しました（{steps.Count}ステップ）。get_scenario_resultで結果を確認してください。";
    }

    [McpServerTool(Name = "get_scenario_result"), Description(
        "run_scenarioで投入したシナリオの実行結果を取得する。まだ完了していない場合はその旨を返す。")]
    public ScenarioResultModel? GetScenarioResult(
        [Description("対象のTier2アプリ名（srm_listに表示される名前）")] string app,
        [Description("結果が出るまで待つ最大秒数（0の場合は今の状態を即座に返す）")] int waitSeconds = 0)
    {
        var runningApp = RunningAppRegistry.Find(app)
            ?? throw new McpException($"実行中のアプリが見つかりません: {app}");

        if (string.IsNullOrWhiteSpace(runningApp.ControlDir))
            throw new McpException($"{app} のcontrolディレクトリが記録されていません。");

        var channel = new SandboxControlChannel(runningApp.ControlDir);
        return waitSeconds > 0
            ? channel.WaitForScenarioResult(TimeSpan.FromSeconds(waitSeconds))
            : channel.TryReadScenarioResult();
    }
}

using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Srm.Runtime.Evidence;
using Srm.Runtime.Logging;

namespace Srm.Mcp.Tools;

// srm evidence list/show/promote/reject相当のMCPツール（DC-013）。Tier2 outboxの
// 検疫データはスキャン結果を自動ゲートにせず、list/showで内容を確認したうえで
// promote/rejectという明示操作でのみユーザー指定先へ反映する方針をMCP経由でも維持する。
[McpServerToolType]
public class EvidenceTools
{
    [McpServerTool(Name = "srm_evidence_list"), Description("Tier2 outboxの検疫中の実行一覧を表示する。")]
    public List<EvidenceRun> List([Description("アプリ名")] string app) =>
        new EvidenceQuarantineStore().List(app);

    [McpServerTool(Name = "srm_evidence_show"), Description("検疫データの詳細（マニフェスト・違反検出）を表示する。")]
    public EvidenceRun Show([Description("アプリ名")] string app, [Description("実行ID")] string runId)
    {
        var run = new EvidenceQuarantineStore().Show(app, runId);
        if (run == null)
            throw new McpException($"検疫データが見つかりません: {app}/{runId}");
        return run;
    }

    [McpServerTool(Name = "srm_evidence_promote"), Description(
        "検疫データを指定先へコピーする（検疫側の控えは監査証跡として残る。moveではなくcopy）。")]
    public string Promote(
        [Description("アプリ名")] string app,
        [Description("実行ID")] string runId,
        [Description("昇格先ディレクトリ")] string dest)
    {
        try
        {
            new EvidenceQuarantineStore().Promote(app, runId, dest);
            new StructuredLogger(app).Info("evidence_promoted", new { run_id = runId, dest });
            return $"昇格しました: {app}/{runId} -> {dest}";
        }
        catch (InvalidOperationException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "srm_evidence_reject"), Description("検疫データを明示的に却下する（削除はしない。監査のため保持される）。")]
    public string Reject([Description("アプリ名")] string app, [Description("実行ID")] string runId)
    {
        try
        {
            new EvidenceQuarantineStore().Reject(app, runId);
            new StructuredLogger(app).Info("evidence_rejected", new { run_id = runId });
            return $"却下しました: {app}/{runId}";
        }
        catch (InvalidOperationException ex)
        {
            throw new McpException(ex.Message);
        }
    }
}

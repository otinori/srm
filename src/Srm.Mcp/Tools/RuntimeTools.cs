using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Srm.Runtime;
using Srm.Runtime.Logging;
using Srm.Runtime.Operations;

namespace Srm.Mcp.Tools;

// srm run/stop/list/logs/validate相当のMCPツール。CLI（Srm.Cli）と同じく
// Srm.Runtime.Operationsを直接呼び出すだけで、CLIをサブプロセスとして起動して
// 標準出力をパースする方式は採らない（DC-017 decision 2）。
[McpServerToolType]
public class RuntimeTools(PoliciesDir policiesDir)
{
    [McpServerTool(Name = "srm_run"), Description(
        "ポリシーに従ってアプリをサンドボックス内で起動する（tierに応じてAppContainerまたはWindows Sandbox）。管理者権限が必要。")]
    public RunOperationResult Run(
        [Description("ポリシー名 (例: claude-code) または .yaml への絶対パス")] string policy,
        [Description("整合性検証をスキップする（開発時のみ）")] bool noIntegrityCheck = false)
    {
        try
        {
            // redirectStdioToNul: true — Srm.Mcp自身のstdoutはMCPのJSON-RPC伝送路
            // そのものであり、対象アプリ（Tier1）のコンソール出力が混入すると
            // プロトコルが壊れる（DC-017 review_trigger、実機で再現確認済み）。
            return new RunOperation().Execute(policiesDir.Path, policy, noIntegrityCheck, redirectStdioToNul: true);
        }
        catch (SrmOperationException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "srm_stop"), Description("実行中のアプリとその子プロセスを終了する。管理者権限が必要。")]
    public StopOperationResult Stop([Description("停止するアプリ名")] string app)
    {
        try
        {
            return new StopOperation().Execute(app);
        }
        catch (SrmOperationException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "srm_list"), Description("実行中のサンドボックスを一覧表示する。")]
    public List<RunningApp> List() => RunningAppRegistry.GetRunning();

    [McpServerTool(Name = "srm_logs"), Description("アプリの構造化ログ（JSON Lines、新しい順）を表示する。")]
    public List<string> Logs(
        [Description("ログを表示するアプリ名")] string app,
        [Description("表示する行数")] int lines = 50) =>
        new StructuredLogger(app).ReadLatest(lines).ToList();

    [McpServerTool(Name = "srm_validate"), Description("ポリシーファイルの構文と整合性を検証する。")]
    public ValidateOperationResult Validate(
        [Description("ポリシー名 (例: claude-code) または .yaml への絶対パス")] string policy,
        [Description("整合性サイドカーを生成・更新する")] bool sign = false)
    {
        try
        {
            return new ValidateOperation().Execute(policiesDir.Path, policy, sign);
        }
        catch (SrmOperationException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (FileNotFoundException ex)
        {
            throw new McpException(ex.Message);
        }
    }
}

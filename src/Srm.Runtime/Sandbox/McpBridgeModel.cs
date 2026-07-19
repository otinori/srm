using System.Text.Json.Serialization;

namespace Srm.Runtime.Sandbox;

// channel-d-guest-mcp-bridge: focus-request.json/diag-request.jsonと同じ
// 「固定パス2ファイルを都度上書きする」方式で、ゲスト内のMCPブリッジスタブと
// ホスト側の--mcp-bridge-hostプロセスが1回のMCPツール呼び出しをやり取りする
// ための最小限の契約。実際のJSON-RPCフレーミング（jsonrpc/id/エラーコード等）は
// この契約の外側（スタブ・ブリッジホストそれぞれの実装）で扱い、ここでは
// 「どのサーバーの・どのツールを・どんな引数で呼ぶか」「成功/失敗と結果」という
// 本質的な部分だけをモデル化する。
public class McpRequestModel
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = "";

    // policy.mcp.allow_serversのnameと一致させる。
    [JsonPropertyName("serverName")]
    public string ServerName { get; set; } = "";

    // "tools/list" または "tools/call"。design.mdのNon-Goalsの通り、
    // 通知・ストリーミング系のMCPメソッドは対象外。
    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    // method=="tools/call"のときのみ使用。
    [JsonPropertyName("toolName")]
    public string? ToolName { get; set; }

    // method=="tools/call"のときのみ使用。ツール引数をそのままJSON文字列として運ぶ
    // （このモデル自体はMCPの引数スキーマを知る必要がないため）。
    [JsonPropertyName("argumentsJson")]
    public string? ArgumentsJson { get; set; }
}

public class McpResultModel
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = "";

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    // successがfalseの場合の理由（allow_servers/allow_tools外・実サーバーのエラー等）。
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    // successがtrueの場合の結果（tools/listの一覧、またはtools/callの戻り値）を
    // そのままJSON文字列として運ぶ。
    [JsonPropertyName("resultJson")]
    public string? ResultJson { get; set; }
}

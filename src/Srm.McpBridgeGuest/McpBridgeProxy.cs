using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Srm.Runtime.Sandbox;

namespace Srm.McpBridgeGuest;

// channel-d-guest-mcp-bridge: 複数のallow_serversを1本のstdio MCPサーバーとして
// 集約する。tools/listは全サーバーへ問い合わせて結果を束ね、ツール名→サーバー名の
// 対応をキャッシュしておく。tools/callはツール名だけを受け取るMCPの制約上、
// このキャッシュを使って対応するサーバーへ振り分ける。
public class McpBridgeProxy
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly SandboxControlChannel _control;
    private readonly string[] _serverNames;
    private readonly Dictionary<string, string> _toolToServer = new();

    public McpBridgeProxy(SandboxControlChannel control, string[] serverNames)
    {
        _control = control;
        _serverNames = serverNames;
    }

    public async Task<IList<Tool>> ListToolsAsync(CancellationToken cancellationToken)
    {
        var tools = new List<Tool>();
        _toolToServer.Clear();

        foreach (var serverName in _serverNames)
        {
            var result = await SendRequestAsync(new McpRequestModel
            {
                RequestId = Guid.NewGuid().ToString(),
                ServerName = serverName,
                Method = "tools/list",
            }, cancellationToken);

            if (!result.Success || result.ResultJson == null)
                continue; // ホスト側で拒否・失敗した場合はそのサーバーのツールを単に見せない

            var entries = JsonSerializer.Deserialize<List<ToolSummary>>(result.ResultJson) ?? new();
            foreach (var entry in entries)
            {
                _toolToServer[entry.Name] = serverName;
                tools.Add(new Tool
                {
                    Name = entry.Name,
                    Description = entry.Description,
                    // ホスト側は入力スキーマを転送しない（design.mdのスコープ外・
                    // tools/callの引数は素通しJSONとして扱う）ため、最小限の空スキーマを
                    // 補う。実際の引数検証は実サーバー自身が行う。
                    InputSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement,
                });
            }
        }

        return tools;
    }

    public async Task<CallToolResult> CallToolAsync(
        string toolName, IDictionary<string, JsonElement>? arguments, CancellationToken cancellationToken)
    {
        if (!_toolToServer.TryGetValue(toolName, out var serverName))
        {
            // クライアントがtools/listを呼ばずにtools/callだけ呼んだ場合に備え、
            // 一度だけ最新のtools/listを取り直してから再判定する。
            await ListToolsAsync(cancellationToken);
            if (!_toolToServer.TryGetValue(toolName, out serverName))
                throw new McpProtocolException($"未知のツールです: '{toolName}'", McpErrorCode.InvalidParams);
        }

        var result = await SendRequestAsync(new McpRequestModel
        {
            RequestId = Guid.NewGuid().ToString(),
            ServerName = serverName,
            Method = "tools/call",
            ToolName = toolName,
            ArgumentsJson = arguments == null ? null : JsonSerializer.Serialize(arguments),
        }, cancellationToken);

        if (!result.Success)
            throw new McpProtocolException(result.Reason ?? "ホスト側ブリッジが呼び出しを拒否しました。", McpErrorCode.InvalidRequest);

        // ホスト側（McpBridgeHostCommand）はCallToolResultをそのままシリアライズしている
        // ため、同じ型へ逆シリアライズするだけで往復できる。
        return JsonSerializer.Deserialize<CallToolResult>(result.ResultJson!)
            ?? throw new McpProtocolException("ホスト側からの結果を解釈できませんでした。", McpErrorCode.InternalError);
    }

    private Task<McpResultModel> SendRequestAsync(McpRequestModel request, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            _control.WriteMcpRequest(request);
            var result = _control.WaitForMcpResult(request.RequestId, RequestTimeout);
            return result ?? new McpResultModel { RequestId = request.RequestId, Success = false, Reason = "ホスト側ブリッジからの応答がタイムアウトしました。" };
        }, cancellationToken);

    // McpBridgeHostCommand.HandleToolsListが匿名型 { name, description }
    // （小文字）でシリアライズしているため、既定の大文字小文字を区別する
    // デシリアライズでは一致しない。明示的にプロパティ名を合わせる
    // （実機E2Eテストで実際に踏んだ不具合: 一致しないとNameが空文字列のまま
    // 静かに失敗し、tools/callが「未知のツール」エラーになる）。
    private class ToolSummary
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("description")]
        public string? Description { get; set; }
    }
}

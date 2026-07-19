using System.Text.Json;
using ModelContextProtocol.Client;
using Srm.PolicyEngine;
using Srm.PolicyEngine.Models;
using Srm.Runtime.Logging;
using Srm.Runtime.Sandbox;

namespace Srm.Cli.Commands;

// channel-d-guest-mcp-bridge: `srm run <app> --mcp-bridge-host --control-dir <dir>
// --mcp-policy-path <path>` の内部モード本体。RunOperation.SpawnMcpBridgeHostから
// 起動される（RunCommand.RunNested/RunQuotaMonitorと同じくCLI固有の経路のため
// RunOperationには抽出しない）。design.mdの通り、実ホストMCPサーバー
// （policy.mcp.allow_servers）を子プロセスとして保持し、ゲスト側スタブが書く
// mcp-request.jsonをポーリングして許可済みの呼び出しだけを実サーバーへ転送する。
public static class McpBridgeHostCommand
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public static void Run(string? appName, string? controlDir, string? policyPath)
    {
        if (string.IsNullOrWhiteSpace(controlDir) || string.IsNullOrWhiteSpace(policyPath))
        {
            Console.Error.WriteLine("--mcp-bridge-host には --control-dir/--mcp-policy-path の指定が必須です。");
            Environment.Exit(1);
            return;
        }

        var logger = new StructuredLogger(string.IsNullOrWhiteSpace(appName) ? "unknown" : appName);
        var policy = new PolicyLoader().Load(policyPath);
        var control = new SandboxControlChannel(controlDir);

        var clients = new Dictionary<string, (McpServerEntry Entry, McpClient Client)>();
        try
        {
            foreach (var server in policy.Mcp.AllowServers)
            {
                try
                {
                    var transport = new StdioClientTransport(new StdioClientTransportOptions
                    {
                        Name = server.Name,
                        Command = server.Command,
                        Arguments = string.IsNullOrWhiteSpace(server.Args)
                            ? Array.Empty<string>()
                            : server.Args.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                    });
                    var client = McpClient.CreateAsync(transport).GetAwaiter().GetResult();
                    clients[server.Name] = (server, client);
                    logger.Info("MCPブリッジ: ホスト側MCPサーバーを起動", new { server = server.Name, command = server.Command });
                }
                catch (Exception ex)
                {
                    logger.Warn("MCPブリッジ: ホスト側MCPサーバーの起動に失敗しました", new { server = server.Name, error = ex.Message });
                }
            }

            string? lastRequestId = null;
            while (!control.IsStopped && !control.IsStopRequested)
            {
                var request = control.TryReadMcpRequest();
                if (request != null && request.RequestId != lastRequestId)
                {
                    lastRequestId = request.RequestId;
                    HandleRequest(request, clients, control, logger);
                }
                Thread.Sleep(PollInterval);
            }
        }
        finally
        {
            foreach (var (_, client) in clients.Values)
            {
                try { client.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch { /* ベストエフォート */ }
            }
        }
    }

    private static void HandleRequest(
        McpRequestModel request,
        Dictionary<string, (McpServerEntry Entry, McpClient Client)> clients,
        SandboxControlChannel control,
        StructuredLogger logger)
    {
        if (!clients.TryGetValue(request.ServerName, out var entry))
        {
            logger.Warn("MCPブリッジ: 許可されていないサーバーへの接続を拒否", new { server = request.ServerName, method = request.Method });
            control.WriteMcpResult(new McpResultModel
            {
                RequestId = request.RequestId,
                Success = false,
                Reason = $"サーバー '{request.ServerName}' は mcp.allow_servers に含まれていません。",
            });
            return;
        }

        try
        {
            switch (request.Method)
            {
                case "tools/list":
                    HandleToolsList(request, entry, control, logger);
                    return;
                case "tools/call":
                    HandleToolsCall(request, entry, control, logger);
                    return;
                default:
                    logger.Warn("MCPブリッジ: 未対応のメソッド", new { server = request.ServerName, method = request.Method });
                    control.WriteMcpResult(new McpResultModel
                    {
                        RequestId = request.RequestId,
                        Success = false,
                        Reason = $"メソッド '{request.Method}' はChannel Dでは未対応です（tools/list・tools/callのみ対応）。",
                    });
                    return;
            }
        }
        catch (Exception ex)
        {
            logger.Warn("MCPブリッジ: 実サーバー呼び出しでエラー", new { server = request.ServerName, method = request.Method, error = ex.Message });
            control.WriteMcpResult(new McpResultModel { RequestId = request.RequestId, Success = false, Reason = ex.Message });
        }
    }

    private static void HandleToolsList(
        McpRequestModel request,
        (McpServerEntry Entry, McpClient Client) entry,
        SandboxControlChannel control,
        StructuredLogger logger)
    {
        var tools = entry.Client.ListToolsAsync().GetAwaiter().GetResult();
        var visible = entry.Entry.AllowTools == null
            ? tools
            : tools.Where(t => entry.Entry.AllowTools.Contains(t.Name)).ToList();

        var json = JsonSerializer.Serialize(visible.Select(t => new { name = t.Name, description = t.Description }));
        logger.Info("MCPブリッジ: tools/list", new { server = request.ServerName, tool_count = visible.Count });
        control.WriteMcpResult(new McpResultModel { RequestId = request.RequestId, Success = true, ResultJson = json });
    }

    private static void HandleToolsCall(
        McpRequestModel request,
        (McpServerEntry Entry, McpClient Client) entry,
        SandboxControlChannel control,
        StructuredLogger logger)
    {
        if (string.IsNullOrWhiteSpace(request.ToolName))
        {
            control.WriteMcpResult(new McpResultModel { RequestId = request.RequestId, Success = false, Reason = "toolName が指定されていません。" });
            return;
        }

        // 仕様（specs/guest-mcp-server-allowlist）: allow_toolsが指定されている
        // サーバーは、そこに含まれないツール呼び出しを実サーバーへ転送してはならない。
        if (entry.Entry.AllowTools != null && !entry.Entry.AllowTools.Contains(request.ToolName))
        {
            logger.Warn("MCPブリッジ: 許可されていないツール呼び出しを拒否", new { server = request.ServerName, tool = request.ToolName });
            control.WriteMcpResult(new McpResultModel
            {
                RequestId = request.RequestId,
                Success = false,
                Reason = $"ツール '{request.ToolName}' はサーバー '{request.ServerName}' の allow_tools に含まれていません。",
            });
            return;
        }

        var args = string.IsNullOrWhiteSpace(request.ArgumentsJson)
            ? new Dictionary<string, object?>()
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(request.ArgumentsJson) ?? new Dictionary<string, object?>();

        var result = entry.Client.CallToolAsync(request.ToolName, args, cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
        var json = JsonSerializer.Serialize(result);
        logger.Info("MCPブリッジ: tools/call許可・実行", new { server = request.ServerName, tool = request.ToolName });
        control.WriteMcpResult(new McpResultModel { RequestId = request.RequestId, Success = true, ResultJson = json });
    }
}

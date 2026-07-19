using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Srm.McpBridgeGuest;
using Srm.Runtime.Sandbox;

// channel-d-guest-mcp-bridge: サンドボックス内エージェントの実際のMCPクライアント
// （.mcp.json等）がこのプロセスをstdio MCPサーバーとして起動する。エージェント視点では
// 通常のMCPサーバーに見えるが、実体はChannel Dのcontrolフォルダ経由でホスト側
// --mcp-bridge-hostへ中継するプロキシ。policy.mcp.allow_servers（コマンド・
// allow_tools等の実際の許可情報）はホスト側だけが知っており、このプロセス自身は
// 「どのサーバー名が存在するか」だけを知っていればよい（多層防御: 万一この
// プロセスが乗っ取られても、実際の許可判定はホスト側ブリッジが最終的に行う）。
var controlDir = Environment.GetEnvironmentVariable("SRM_MCP_CONTROL_DIR");
var serverNamesRaw = Environment.GetEnvironmentVariable("SRM_MCP_SERVERS");

if (string.IsNullOrWhiteSpace(controlDir) || string.IsNullOrWhiteSpace(serverNamesRaw))
{
    Console.Error.WriteLine("環境変数 SRM_MCP_CONTROL_DIR / SRM_MCP_SERVERS の指定が必須です。");
    Environment.Exit(1);
    return;
}

var serverNames = serverNamesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var control = new SandboxControlChannel(controlDir);
var bridge = new McpBridgeProxy(control, serverNames);

// 【実機で判明した重要な結果】Host.CreateApplicationBuilder(args)の既定設定は、
// appsettings.json等のホットリロード用にカレントディレクトリへFileSystemWatcherを
// 張ろうとする。Srm.Mcp.exe（ホスト側で動く、AppContainer外）ではこれが問題に
// ならなかったが、このプロセスはAppContainer内で動く前提であり、起動時の
// カレントディレクトリ（allow_pathsに含まれない可能性が高い）への読み取りが
// ACCESS_DENIEDになり、FileSystemWatcher初期化がFileNotFoundExceptionで
// クラッシュすることを実機で確認した。このプロセスは設定ファイルを一切使わない
// （SRM_MCP_CONTROL_DIR/SRM_MCP_SERVERSは環境変数のみで完結）ため、
// DisableDefaults=trueでファイルベースの既定設定を丸ごと無効化し、
// 必要なもの（コンソールログ）だけを明示的に追加する。
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    DisableDefaults = true,
});

// stdio transportではstdoutがJSON-RPCの伝送路そのものなので、既定のコンソールログ
// （stdoutに出る）を混ぜるとプロトコルが壊れる（Srm.Mcp.Program.csと同じ注意）。
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithListToolsHandler(async (request, cancellationToken) =>
        new ListToolsResult { Tools = await bridge.ListToolsAsync(cancellationToken) })
    .WithCallToolHandler(async (request, cancellationToken) =>
        await bridge.CallToolAsync(request.Params!.Name, request.Params.Arguments, cancellationToken));

var app = builder.Build();
await app.RunAsync();

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Srm.Mcp.Tools;
using Srm.Runtime;

var policiesDir = PolicyPathResolver.ResolvePoliciesDir(AppContext.BaseDirectory);

var builder = Host.CreateApplicationBuilder(args);

// stdio transportではstdoutがJSON-RPCの伝送路そのものなので、既定のコンソールログ
// （stdoutに出る）を混ぜるとプロトコルが壊れる。ログは必ずstderrへ回す。
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(new PoliciesDir(policiesDir));
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var app = builder.Build();
await app.RunAsync();

using System.CommandLine;
using Srm.Runtime.Logging;

namespace Srm.Cli.Commands;

public static class LogsCommand
{
    public static Command Build()
    {
        var appArg = new Argument<string>("app", "ログを表示するアプリ名");
        var linesOption = new Option<int>("--lines", () => 50, "表示する行数");
        linesOption.AddAlias("-n");

        var cmd = new Command("logs", "アプリのログを表示する") { appArg, linesOption };

        cmd.SetHandler((string appName, int lines) =>
        {
            var logger = new StructuredLogger(appName);
            var entries = logger.ReadLatest(lines).ToList();

            if (entries.Count == 0)
            {
                Console.WriteLine($"ログがありません: {appName}");
                return;
            }

            foreach (var line in entries)
                Console.WriteLine(line);
        }, appArg, linesOption);

        return cmd;
    }
}

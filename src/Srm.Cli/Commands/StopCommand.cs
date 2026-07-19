using System.CommandLine;
using Srm.Runtime.Operations;

namespace Srm.Cli.Commands;

public static class StopCommand
{
    public static Command Build()
    {
        var appArg = new Argument<string>("app", "停止するアプリ名");
        var cmd = new Command("stop", "実行中のアプリを停止し、WFPフィルターを削除する") { appArg };

        cmd.SetHandler((string appName) =>
        {
            try
            {
                new StopOperation().Execute(appName);
                Console.WriteLine($"停止しました: {appName}");
            }
            catch (SrmOperationException ex)
            {
                Console.Error.WriteLine(ex.Message);
                Environment.Exit(ex.ExitCode);
            }
        }, appArg);

        return cmd;
    }
}

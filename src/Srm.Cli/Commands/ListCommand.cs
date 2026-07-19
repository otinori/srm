using System.CommandLine;
using Srm.Runtime;

namespace Srm.Cli.Commands;

public static class ListCommand
{
    public static Command Build()
    {
        var cmd = new Command("list", "実行中のアプリを一覧表示する");

        cmd.SetHandler(() =>
        {
            var apps = RunningAppRegistry.GetRunning();
            if (apps.Count == 0)
            {
                Console.WriteLine("実行中のアプリはありません");
                return;
            }

            Console.WriteLine($"{"NAME",-20} {"PID",8}  {"STARTED",-25}  POLICY");
            Console.WriteLine(new string('-', 80));
            foreach (var app in apps)
                Console.WriteLine($"{app.Name,-20} {app.Pid,8}  {app.StartedAt:yyyy-MM-dd HH:mm:ss zzz,-25}  {app.PolicyPath}");
        });

        return cmd;
    }
}

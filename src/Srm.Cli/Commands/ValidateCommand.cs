using System.CommandLine;
using Srm.Runtime.Operations;

namespace Srm.Cli.Commands;

public static class ValidateCommand
{
    public static Command Build(string policiesDir)
    {
        var policyArg = new Argument<string>("policy", "ポリシー名 (例: claude-code) または .yaml への絶対パス");
        var signOption = new Option<bool>("--sign", "サイドカーを生成・更新する");

        var cmd = new Command("validate", "ポリシーファイルの構文と権限を検証する") { policyArg, signOption };

        cmd.SetHandler((string policyName, bool sign) =>
        {
            try
            {
                var result = new ValidateOperation().Execute(policiesDir, policyName, sign);

                if (result.SidecarPath != null)
                    Console.WriteLine($"署名しました: {result.SidecarPath}");

                foreach (var warning in result.Warnings)
                    Console.WriteLine($"警告: {warning}");

                Console.WriteLine($"OK: {policyName} は有効なポリシーです (tier={result.Tier}, exe={result.Executable})");
            }
            catch (SrmOperationException ex)
            {
                Console.Error.WriteLine(ex.Message);
                Environment.Exit(ex.ExitCode);
            }
        }, policyArg, signOption);

        return cmd;
    }
}

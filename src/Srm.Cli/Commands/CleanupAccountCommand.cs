using System.CommandLine;
using Srm.Runtime.Operations;

namespace Srm.Cli.Commands;

public static class CleanupAccountCommand
{
    public static Command Build()
    {
        var policyArg = new Argument<string>("policy",
            "restricted-account-app-isolation（tier2.app_container: false）用に作成された専用アカウントを削除する対象のポリシー名");
        var cmd = new Command("cleanup-account",
            "tier2.app_container: false 用に作成された専用ローカルアカウントを削除する（アカウントはsrm stopでは削除されないため）")
        { policyArg };

        cmd.SetHandler((string policyName) =>
        {
            try
            {
                var result = new CleanupAccountOperation().Execute(policyName);
                Console.WriteLine($"専用アカウントを削除しました: {result.PolicyName}");
            }
            catch (SrmOperationException ex)
            {
                Console.Error.WriteLine(ex.Message);
                Environment.Exit(ex.ExitCode);
            }
        }, policyArg);

        return cmd;
    }
}

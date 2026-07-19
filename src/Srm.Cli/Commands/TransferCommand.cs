using System.CommandLine;
using Srm.Runtime.Operations;

namespace Srm.Cli.Commands;

// tier2-channel-c-mapped-folder: tier2-file-transfer capabilityのCLI入口。
public static class TransferCommand
{
    public static Command Build()
    {
        var root = new Command("transfer", "実行中のTier2ゲストとホスト間でファイルを転送する（オンデマンド、双方向）");

        root.AddCommand(BuildPut());
        root.AddCommand(BuildGet());

        return root;
    }

    private static Command BuildPut()
    {
        var appArg = new Argument<string>("app", "対象アプリ名（srm listに表示される名前、Tier2のみ）");
        var hostSourceArg = new Argument<string>("host-source", "転送元のホスト側ファイルパス");
        var guestDestArg = new Argument<string>("guest-dest", "転送先のゲスト側絶対パス（対象ポリシーのfilesystem.allow_paths配下でなければならない）");
        var cmd = new Command("put", "ホスト側のファイルを実行中のTier2ゲストへ転送する")
        {
            appArg, hostSourceArg, guestDestArg,
        };

        cmd.SetHandler((string appName, string hostSource, string guestDest) =>
        {
            try
            {
                var result = new FileTransferOperation().Put(appName, hostSource, guestDest);
                if (result.Success)
                {
                    Console.WriteLine($"転送しました: {hostSource} -> {appName}:{guestDest}");
                }
                else
                {
                    Console.Error.WriteLine($"転送に失敗しました: {result.Reason}");
                    Environment.Exit(1);
                }
            }
            catch (SrmOperationException ex)
            {
                Console.Error.WriteLine(ex.Message);
                Environment.Exit(ex.ExitCode);
            }
        }, appArg, hostSourceArg, guestDestArg);

        return cmd;
    }

    private static Command BuildGet()
    {
        var appArg = new Argument<string>("app", "対象アプリ名（srm listに表示される名前、Tier2のみ）");
        var guestSourceArg = new Argument<string>("guest-source", "転送元のゲスト側絶対パス（対象ポリシーのfilesystem.allow_paths配下でなければならない）");
        var cmd = new Command("get",
            "実行中のTier2ゲスト内のファイルをoutbox\\transfer\\配下へ配置する。" +
            "ユーザーの作業ディレクトリへは自動反映されない（DC-013の検疫→srm evidence promoteを経由すること）")
        {
            appArg, guestSourceArg,
        };

        cmd.SetHandler((string appName, string guestSource) =>
        {
            try
            {
                var result = new FileTransferOperation().Get(appName, guestSource);
                if (result.Success)
                {
                    Console.WriteLine($"outboxへ配置しました: {appName}:{guestSource} (srm stop後にsrm evidenceで確認できます)");
                }
                else
                {
                    Console.Error.WriteLine($"転送に失敗しました: {result.Reason}");
                    Environment.Exit(1);
                }
            }
            catch (SrmOperationException ex)
            {
                Console.Error.WriteLine(ex.Message);
                Environment.Exit(ex.ExitCode);
            }
        }, appArg, guestSourceArg);

        return cmd;
    }
}

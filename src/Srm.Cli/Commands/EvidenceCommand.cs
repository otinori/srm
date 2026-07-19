using System.CommandLine;
using Srm.Runtime.Evidence;

namespace Srm.Cli.Commands;

// outboxの検疫〜昇格パイプライン（DC-013）のCLI。スキャン結果は自動ゲートにせず、
// list/show で内容を確認したうえで promote/reject という人間の明示操作でのみ
// ユーザー指定先へ反映する。
public static class EvidenceCommand
{
    public static Command Build()
    {
        var root = new Command("evidence", "Tier2 outboxの検疫データを操作する");

        root.AddCommand(BuildList());
        root.AddCommand(BuildShow());
        root.AddCommand(BuildPromote());
        root.AddCommand(BuildReject());

        return root;
    }

    private static Command BuildList()
    {
        var appArg = new Argument<string>("app", "アプリ名");
        var cmd = new Command("list", "検疫中の実行一覧を表示する") { appArg };

        cmd.SetHandler((string app) =>
        {
            var runs = new EvidenceQuarantineStore().List(app);
            if (runs.Count == 0)
            {
                Console.WriteLine($"{app} の検疫データはありません");
                return;
            }

            Console.WriteLine($"{"RUN ID",-20} {"STATE",-12} {"FILES",6} {"SIZE(B)",10}  QUARANTINED AT");
            Console.WriteLine(new string('-', 80));
            foreach (var run in runs)
                Console.WriteLine($"{run.RunId,-20} {run.State,-12} {run.FileCount,6} {run.TotalSizeBytes,10}  {run.QuarantinedAt:yyyy-MM-dd HH:mm:ss zzz}");
        }, appArg);

        return cmd;
    }

    private static Command BuildShow()
    {
        var appArg = new Argument<string>("app", "アプリ名");
        var runIdArg = new Argument<string>("runid", "実行ID");
        var cmd = new Command("show", "検疫データの詳細（マニフェスト・違反検出）を表示する") { appArg, runIdArg };

        cmd.SetHandler((string app, string runId) =>
        {
            var run = new EvidenceQuarantineStore().Show(app, runId);
            if (run == null)
            {
                Console.Error.WriteLine($"検疫データが見つかりません: {app}/{runId}");
                Environment.Exit(1);
                return;
            }

            Console.WriteLine($"app: {run.App}");
            Console.WriteLine($"run_id: {run.RunId}");
            Console.WriteLine($"state: {run.State}");
            Console.WriteLine($"quarantined_at: {run.QuarantinedAt:yyyy-MM-dd HH:mm:ss zzz}");
            Console.WriteLine($"file_count: {run.FileCount}");
            Console.WriteLine($"total_size_bytes: {run.TotalSizeBytes}");

            if (run.Violations.Count > 0)
            {
                Console.WriteLine("violations（検疫データに取り込まれなかったファイル）:");
                foreach (var v in run.Violations)
                    Console.WriteLine($"  - {v.RelativePath}: {v.Reason}");
            }
        }, appArg, runIdArg);

        return cmd;
    }

    private static Command BuildPromote()
    {
        var appArg = new Argument<string>("app", "アプリ名");
        var runIdArg = new Argument<string>("runid", "実行ID");
        var destOption = new Option<string>("--dest", "昇格先ディレクトリ") { IsRequired = true };
        var cmd = new Command("promote", "検疫データを指定先へコピーする（検疫側の控えは監査証跡として残す）")
        {
            appArg, runIdArg, destOption,
        };

        cmd.SetHandler((string app, string runId, string dest) =>
        {
            try
            {
                new EvidenceQuarantineStore().Promote(app, runId, dest);
                Console.WriteLine($"昇格しました: {app}/{runId} -> {dest}");
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"[エラー] {ex.Message}");
                Environment.Exit(1);
            }
        }, appArg, runIdArg, destOption);

        return cmd;
    }

    private static Command BuildReject()
    {
        var appArg = new Argument<string>("app", "アプリ名");
        var runIdArg = new Argument<string>("runid", "実行ID");
        var cmd = new Command("reject", "検疫データを明示的に却下する（削除はしない。監査のため保持される）")
        {
            appArg, runIdArg,
        };

        cmd.SetHandler((string app, string runId) =>
        {
            try
            {
                new EvidenceQuarantineStore().Reject(app, runId);
                Console.WriteLine($"却下しました: {app}/{runId}");
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"[エラー] {ex.Message}");
                Environment.Exit(1);
            }
        }, appArg, runIdArg);

        return cmd;
    }
}

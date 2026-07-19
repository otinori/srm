using System.Diagnostics;
using System.IO.Compression;
using Srm.PolicyEngine.Models;

namespace Srm.Runtime.Sandbox;

// DC-012: ゲスト内でprovision.stepsを解釈・実行する。srmはツールチェーンの取得・
// バージョン解決を行わず、ホストが事前キャッシュ・ROマウントしたzipを展開し
// PATHへ追加するだけの「宣言的な実行」に責務を限定する。対応するステップは
// "unzip: <toolchain名>"（provision.toolchainのnameと対応）と"run: <コマンド>"の
// 2種類のみ（設計文書のYAML例に準拠）。
public class Provisioner
{
    // ゲスト内のみに存在する作業領域。ホストへはマップされないため、VMが閉じれば
    // 消える（Windows Sandboxは既定でステートレス）。
    public const string GuestWorkRoot = @"C:\SrmProvision";

    public void Run(ProvisionPolicy provision, string guestSourceRoot, string? workingDirectory, Action<string> log)
    {
        if (provision.Steps.Count == 0)
            return;

        Directory.CreateDirectory(GuestWorkRoot);

        foreach (var step in provision.Steps)
        {
            if (step.StartsWith("unzip:", StringComparison.OrdinalIgnoreCase))
            {
                ExtractToolchain(step["unzip:".Length..].Trim(), guestSourceRoot, log);
            }
            else if (step.StartsWith("run:", StringComparison.OrdinalIgnoreCase))
            {
                RunShellCommand(step["run:".Length..].Trim(), workingDirectory, log);
            }
            else
            {
                log($"[警告] 未知のprovisionステップ形式です（無視します）: {step}");
            }
        }
    }

    private static void ExtractToolchain(string name, string guestSourceRoot, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("provision.stepsの'unzip:'にツールチェーン名が指定されていません。");

        var sourceDir = Path.Combine(guestSourceRoot, name);
        if (!Directory.Exists(sourceDir))
            throw new InvalidOperationException(
                $"provision対象のツールチェーンが見つかりません: {sourceDir}" +
                "（ホスト側の %ProgramData%\\SRM\\toolchain-cache\\<name>\\<version>\\ に事前配置が必要）");

        var zipFiles = Directory.GetFiles(sourceDir, "*.zip", SearchOption.AllDirectories);
        if (zipFiles.Length == 0)
            throw new InvalidOperationException($"provision対象のzipファイルが見つかりません: {sourceDir}");

        var destDir = Path.Combine(GuestWorkRoot, name);
        if (Directory.Exists(destDir))
            Directory.Delete(destDir, recursive: true);

        log($"展開します: {zipFiles[0]} -> {destDir}");
        ZipFile.ExtractToDirectory(zipFiles[0], destDir);

        // 展開したツールチェーンのルートをPATH先頭へ追加する。実行ファイルがzip直下に
        // 無くサブフォルダに1段だけ入っている構成（node公式配布等）も想定し、直下の
        // フォルダも合わせて追加する（DC-012: srmの責務はマウント・展開の宣言的な
        // 実行に限定し、正確なレイアウト検証まではしない）。
        var pathEntries = new List<string> { destDir };
        pathEntries.AddRange(Directory.EnumerateDirectories(destDir));

        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        Environment.SetEnvironmentVariable("PATH", string.Join(';', pathEntries) + ";" + currentPath);
        log($"PATHへ追加しました: {string.Join(", ", pathEntries)}");
    }

    private static void RunShellCommand(string command, string? workingDirectory, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(command))
            throw new InvalidOperationException("provision.stepsの'run:'にコマンドが指定されていません。");

        log($"実行します: {command}");

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(command);
        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
            psi.WorkingDirectory = workingDirectory;

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"provisionコマンドの起動に失敗しました: {command}");

        // LogonCommand経由の呼び出しでは有効なコンソールが無いため、出力は明示的に
        // 読み取ってlog経由で書き出す（SandboxLauncherのnested.log方式と同じ理由）。
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (!string.IsNullOrEmpty(stdout)) log(stdout.TrimEnd());
        if (!string.IsNullOrEmpty(stderr)) log(stderr.TrimEnd());

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"provisionコマンドが失敗しました（exit code {proc.ExitCode}）: {command}");

        log($"完了しました（exit code 0）: {command}");
    }
}

using System.Diagnostics;

namespace Srm.Runtime.Evidence;

// DC-013: MpCmdRun.exeによる静的スキャン。結果は合否判定のゲートには使わず、
// ManifestEntry.ScanResultへメタデータとして記録するだけに留める。
// Windows専用APIに依存するためEvidence配下の他クラスと異なりLinux上では動作しない
// （呼び出し側でオプションのデリゲートとして注入し、テストでは未使用にできる）。
public static class WindowsDefenderScanner
{
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(60);

    public static string? FindExecutable()
    {
        var programFiles = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
        if (File.Exists(programFiles))
            return programFiles;

        // Windows Update経由でプラットフォームが更新されている場合、実体は
        // ProgramData配下のバージョン別ディレクトリにある。
        var platformRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Microsoft", "Windows Defender", "Platform");
        if (!Directory.Exists(platformRoot))
            return null;

        var latest = Directory.EnumerateDirectories(platformRoot)
            .OrderByDescending(d => d)
            .FirstOrDefault();
        if (latest == null)
            return null;

        var exe = Path.Combine(latest, "MpCmdRun.exe");
        return File.Exists(exe) ? exe : null;
    }

    // 戻り値はゲート判定には使わない記録用ラベル。呼び出し元がDefenderを見つけられない・
    // 起動に失敗した場合でも例外を投げず、その旨のラベルを返すだけにする
    // （DC-013: スキャン結果はメタデータであり、検疫パイプライン自体を止めてはならない）。
    public static string Scan(string filePath)
    {
        var exe = FindExecutable();
        if (exe == null)
            return "scan_unavailable";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-Scan");
            psi.ArgumentList.Add("-ScanType");
            psi.ArgumentList.Add("3");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(filePath);
            psi.ArgumentList.Add("-DisableRemediation");

            using var proc = Process.Start(psi);
            if (proc == null)
                return "scan_failed";

            if (!proc.WaitForExit((int)ScanTimeout.TotalMilliseconds))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ベストエフォート */ }
                return "scan_timeout";
            }

            // MpCmdRun.exeは脅威検出時0以外のコードを返す。正確な意味はコード毎に
            // 非公開のため、0以外は一律「要確認」として記録するに留める。
            return proc.ExitCode == 0 ? "clean" : $"flagged(exit_code={proc.ExitCode})";
        }
        catch (Exception ex)
        {
            return $"scan_failed({ex.GetType().Name})";
        }
    }
}

namespace Srm.Runtime.Evidence;

public class SanitizeViolation
{
    public string RelativePath { get; set; } = "";
    public string Reason { get; set; } = "";
}

// outboxはTier2の脅威モデル上「信用できない生成物」として扱う（DC-013）。
// 検疫に取り込む前に、ホストの外へ影響しうる要素を検出する。ここでの検出結果は
// 「取り込まない」判断に使うだけで、スキャン結果のような合否ラベルとしては扱わない。
public static class EvidenceSanitizer
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // Windowsの伝統的なMAX_PATH。長いパス対応が有効な環境でも、想定外に深い
    // ディレクトリ構造はそれ自体が異常な生成物である可能性が高いため検出対象にする。
    private const int MaxPathLength = 260;

    // Windowsの予約デバイス名（"CON.txt"等）が絡むパスは、Path.GetRelativePath/GetFullPath
    // 経由だとWin32のパス正規化が"\\.\CON"のような別物へ書き換えてしまい、しかも列挙元
    // （EnumerateFiles/EnumerateFileSystemEntries）によって結果が一致しないことがある
    // （実機で確認済み）。sourceDirが常にentryの先頭に一致する前提を使い、正規化APIを
    // 経由しない単純な文字列切り出しで相対パスを求めることでこれを回避する。
    public static string GetSafeRelativePath(string sourceDir, string entry)
    {
        var baseDir = sourceDir.TrimEnd('\\', '/');
        if (entry.Length > baseDir.Length && entry.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
            return entry[(baseDir.Length + 1)..].Replace('\\', '/');
        return entry.Replace('\\', '/');
    }

    public static List<SanitizeViolation> Scan(string sourceDir)
    {
        var violations = new List<SanitizeViolation>();
        if (!Directory.Exists(sourceDir))
            return violations;

        foreach (var entry in Directory.EnumerateFileSystemEntries(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = GetSafeRelativePath(sourceDir, entry);

            // 予約デバイス名・パス長のチェックは純粋な文字列操作のみで、Win32の
            // レガシーパス解釈を経由しない。File.GetAttributesはCON.txt等の予約名
            // に対して例外を投げることがある（デバイスとして解釈されるため）ため、
            // 先にこれらのチェックを済ませてから試みる（実機で判明：以前はこの
            // 例外がcatch{continue;}で握りつぶされ、予約名検出自体が丸ごと
            // スキップされていた）。
            var nameWithoutExt = Path.GetFileNameWithoutExtension(entry);
            if (ReservedNames.Contains(nameWithoutExt))
                violations.Add(new SanitizeViolation
                {
                    RelativePath = rel,
                    Reason = $"Windowsの予約デバイス名です: {nameWithoutExt}",
                });

            if (entry.Length > MaxPathLength)
                violations.Add(new SanitizeViolation
                {
                    RelativePath = rel,
                    Reason = $"パス長が{MaxPathLength}文字を超えています",
                });

            FileAttributes attrs;
            try { attrs = File.GetAttributes(entry); }
            catch { continue; }

            if ((attrs & FileAttributes.ReparsePoint) != 0)
                violations.Add(new SanitizeViolation
                {
                    RelativePath = rel,
                    Reason = "シンボリックリンク/ジャンクション(reparse point)は検疫に取り込めません",
                });
        }

        return violations;
    }
}

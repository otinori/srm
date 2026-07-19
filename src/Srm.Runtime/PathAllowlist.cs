namespace Srm.Runtime;

// tier2-channel-c-mapped-folder: tier2-file-transferのゲスト側パス
// （転送先/転送元）がpolicy.filesystem.allow_pathsの範囲内であることを検証する
// （design.mdのRisks: パストラバーサルを悪用した任意ファイル読み書きの防止）。
public static class PathAllowlist
{
    public static bool IsWithinAllowedPaths(string candidatePath, IEnumerable<string> allowPaths)
    {
        string fullCandidate;
        try { fullCandidate = Path.GetFullPath(candidatePath); }
        catch { return false; }

        foreach (var allowed in allowPaths)
        {
            string fullAllowed;
            try { fullAllowed = Path.GetFullPath(allowed); }
            catch { continue; }

            if (string.Equals(fullCandidate, fullAllowed, StringComparison.OrdinalIgnoreCase))
                return true;

            // ディレクトリ境界を跨いだ部分一致を避ける（例: "C:\srm\out"が
            // "C:\srm\outbox"に誤ってマッチしないよう、区切り文字込みで比較する）。
            var prefix = fullAllowed.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (fullCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    // Input/Outboxのtransfer/<requestId>/配下に置く実体ファイル名の検証。
    // ディレクトリ区切りを含む値（"..\..\evil.exe"等）を拒否する。
    public static bool IsBareFileName(string fileName) =>
        !string.IsNullOrWhiteSpace(fileName) && Path.GetFileName(fileName) == fileName;
}

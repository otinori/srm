namespace Srm.Runtime;

public static class PolicyPathResolver
{
    /// <summary>絶対パスならそのまま、そうでなければ policiesDir 配下の &lt;name&gt;.yaml として解決する。</summary>
    public static string Resolve(string policiesDir, string name) =>
        Path.IsPathRooted(name) ? name : Path.Combine(policiesDir, name + ".yaml");

    /// <summary>
    /// 実行ファイルの隣・実行ファイル直下・カレントディレクトリの順で policies/ を探す
    /// （publish後のレイアウトとdotnet run実行時の両方に対応するための既定探索順）。
    /// </summary>
    public static string ResolvePoliciesDir(string baseDirectory)
    {
        var candidate = Path.Combine(baseDirectory, "..", "policies");
        if (Directory.Exists(candidate)) return candidate;

        candidate = Path.Combine(baseDirectory, "policies");
        if (Directory.Exists(candidate)) return candidate;

        return Path.Combine(Directory.GetCurrentDirectory(), "policies");
    }
}

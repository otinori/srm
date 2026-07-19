using System.IO;

namespace Srm.PolicyEditor.Services;

/// <summary>
/// srm.exe (Srm.Cli/Program.cs) と同じ解決順序で policies/ ディレクトリの既定値を特定する。
/// UI から任意のフォルダに切り替え可能（このツールを起動している間、アプリ全体で共有される）。
/// </summary>
public class PolicyDirectoryResolver
{
    public string PoliciesDirectory { get; set; }

    public PolicyDirectoryResolver()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "..", "policies");
        if (!Directory.Exists(dir))
            dir = Path.Combine(AppContext.BaseDirectory, "policies");
        if (!Directory.Exists(dir))
            dir = Path.Combine(Directory.GetCurrentDirectory(), "policies");

        PoliciesDirectory = dir;
    }

    public IEnumerable<string> ListPolicyFiles() =>
        Directory.Exists(PoliciesDirectory)
            ? Directory.EnumerateFiles(PoliciesDirectory, "*.yaml").OrderBy(f => f)
            : Enumerable.Empty<string>();

    public string GetPolicyPath(string name) =>
        Path.Combine(PoliciesDirectory, name + ".yaml");
}

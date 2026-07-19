namespace Srm.Runtime.Sandbox;

// Tier2の1回の実行に対応するホスト側ディレクトリレイアウト（DC-010）。
// input/outbox/control はそれぞれ .wsb の MappedFolders としてゲストへ
// バインドマウントされる。
public class SandboxRunPaths
{
    public string RunRoot { get; }
    public string Input { get; }
    public string Outbox { get; }
    public string Control { get; }

    public SandboxRunPaths(string app, string runId, string? root = null)
    {
        var baseRoot = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SRM", "run");
        RunRoot = Path.Combine(baseRoot, app, runId);
        Input = Path.Combine(RunRoot, "input");
        Outbox = Path.Combine(RunRoot, "outbox");
        Control = Path.Combine(RunRoot, "control");
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Input);
        Directory.CreateDirectory(Outbox);
        Directory.CreateDirectory(Control);
    }
}

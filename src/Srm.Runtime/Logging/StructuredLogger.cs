using System.Text.Json;

namespace Srm.Runtime.Logging;

public class StructuredLogger
{
    private readonly string _appName;
    private readonly int _retentionDays;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public StructuredLogger(string appName, int retentionDays = 7)
    {
        _appName = appName;
        _retentionDays = retentionDays;
    }

    private string LogDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SRM", "logs", _appName);

    private string TodayLog => Path.Combine(LogDir, $"{DateTimeOffset.Now:yyyy-MM-dd}.jsonl");

    public void Log(string level, string message, object? extra = null)
    {
        Directory.CreateDirectory(LogDir);
        Rotate();

        var entry = new Dictionary<string, object?>
        {
            ["ts"] = DateTimeOffset.UtcNow.ToString("o"),
            ["level"] = level,
            ["app"] = _appName,
            ["msg"] = message,
        };
        if (extra != null) entry["extra"] = extra;

        var line = JsonSerializer.Serialize(entry, JsonOpts);
        File.AppendAllText(TodayLog, line + "\n");
    }

    public void Info(string msg, object? extra = null) => Log("info", msg, extra);
    public void Warn(string msg, object? extra = null) => Log("warn", msg, extra);
    public void Error(string msg, object? extra = null) => Log("error", msg, extra);

    private void Rotate()
    {
        try
        {
            var cutoff = DateTimeOffset.Now.AddDays(-_retentionDays);
            foreach (var f in Directory.GetFiles(LogDir, "*.jsonl"))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (DateTimeOffset.TryParse(name, out var d) && d < cutoff)
                    File.Delete(f);
            }
        }
        catch { /* ローテーション失敗は無視 */ }
    }

    public IEnumerable<string> ReadLatest(int lines = 100)
    {
        if (!Directory.Exists(LogDir)) yield break;

        var files = Directory.GetFiles(LogDir, "*.jsonl")
            .OrderByDescending(f => f)
            .Take(3);

        var all = new List<string>();
        foreach (var f in files)
        {
            try { all.AddRange(File.ReadAllLines(f)); }
            catch { }
        }
        foreach (var line in all.TakeLast(lines))
            yield return line;
    }
}

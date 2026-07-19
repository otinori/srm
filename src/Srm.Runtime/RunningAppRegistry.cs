using System.Text.Json;
using System.Text.Json.Serialization;

namespace Srm.Runtime;

public class RunningApp
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("pid")]
    public int Pid { get; set; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset StartedAt { get; set; }

    [JsonPropertyName("policy_path")]
    public string PolicyPath { get; set; } = "";

    [JsonPropertyName("tier")]
    public int Tier { get; set; } = 1;

    // Tier2のみ設定される。stop時にSandboxControlChannel経由で穏当な停止を試みるために使う。
    [JsonPropertyName("control_dir")]
    public string? ControlDir { get; set; }

    // Tier2のみ設定される。stop時にoutboxの内容を検疫ストアへ取り込むために使う（DC-013）。
    [JsonPropertyName("run_id")]
    public string? RunId { get; set; }

    [JsonPropertyName("outbox_dir")]
    public string? OutboxDir { get; set; }

    // Tier2のみ設定される。tier2-channel-c-mapped-folderのtier2-file-transferが
    // ホスト→ゲスト方向の転送元コピーを`input/transfer/<requestId>/`へ配置するために使う。
    [JsonPropertyName("input_dir")]
    public string? InputDir { get; set; }

    // Tier1のみ設定される。WindowGuard（DC-017）がsend_key等の呼び出し時に
    // OpenJobObjectで再オープンし、対象ウィンドウの所有PIDがこのJob Object配下に
    // 実在するかを確認するために使う。
    [JsonPropertyName("job_name")]
    public string? JobName { get; set; }

    // channel-d-guest-mcp-bridge: mcp.allow_serversを指定したTier1ポリシーのみ
    // 設定される。stop時に--mcp-bridge-hostのcontrolチャネルへSignalStopするために
    // 使う（Tier2は既存のcontrol_dirを同じ目的で兼用しているため別フィールド不要）。
    [JsonPropertyName("mcp_control_dir")]
    public string? McpControlDir { get; set; }
}

public class RunningAppRegistry
{
    private static readonly string RegistryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SRM", "running.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static void Register(RunningApp app)
    {
        var list = Load();
        list.RemoveAll(a => a.Name == app.Name);
        list.Add(app);
        Save(list);
    }

    public static void Unregister(string appName)
    {
        var list = Load();
        list.RemoveAll(a => a.Name == appName);
        Save(list);
    }

    public static List<RunningApp> GetRunning()
    {
        var list = Load();
        var alive = list.Where(a => IsAlive(a.Pid)).ToList();
        if (alive.Count != list.Count) Save(alive);
        return alive;
    }

    public static RunningApp? Find(string appName) =>
        GetRunning().FirstOrDefault(a => a.Name.Equals(appName, StringComparison.OrdinalIgnoreCase));

    private static bool IsAlive(int pid)
    {
        try
        {
            var proc = System.Diagnostics.Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch { return false; }
    }

    private static List<RunningApp> Load()
    {
        if (!File.Exists(RegistryPath)) return new();
        try
        {
            var json = File.ReadAllText(RegistryPath);
            return JsonSerializer.Deserialize<List<RunningApp>>(json) ?? new();
        }
        catch { return new(); }
    }

    private static void Save(List<RunningApp> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RegistryPath)!);
        File.WriteAllText(RegistryPath, JsonSerializer.Serialize(list, JsonOpts));
    }
}

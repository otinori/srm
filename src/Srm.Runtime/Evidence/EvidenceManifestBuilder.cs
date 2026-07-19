using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Srm.Runtime.Evidence;

public class ManifestEntry
{
    [JsonPropertyName("relative_path")]
    public string RelativePath { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; set; }

    // Windows Defender等のスキャン結果ラベル。合否判定には使わない（DC-013）。
    [JsonPropertyName("scan_result")]
    public string? ScanResult { get; set; }
}

public class Manifest
{
    [JsonPropertyName("app")]
    public string App { get; set; } = "";

    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = "";

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("files")]
    public List<ManifestEntry> Files { get; set; } = new();
}

// outbox配下のファイルからSHA256マニフェストを生成する（DC-013）。DC-003のSHA256
// サイドカー方式と同じ「ハッシュで改ざん・内容を検証可能にする」考え方を踏襲する。
public class EvidenceManifestBuilder
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    // scanFileは任意（DC-013のWindows Defenderスキャン、6.7）。未指定ならScanResultは
    // nullのまま。Windows専用APIを直接参照しないことで、Evidence配下の他クラスと
    // 同様にLinux上でも純粋なファイルI/Oとしてテスト可能な状態を保つ。
    public Manifest Build(string app, string runId, string sourceDir, Func<string, string>? scanFile = null)
    {
        var manifest = new Manifest { App = app, RunId = runId, CreatedAt = DateTimeOffset.Now };

        if (!Directory.Exists(sourceDir))
            return manifest;

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            manifest.Files.Add(new ManifestEntry
            {
                RelativePath = Path.GetRelativePath(sourceDir, file).Replace('\\', '/'),
                Sha256 = ComputeSha256(file),
                SizeBytes = info.Length,
                ScanResult = scanFile?.Invoke(file),
            });
        }

        return manifest;
    }

    public static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    public void WriteManifest(Manifest manifest, string destPath)
    {
        File.WriteAllText(destPath, JsonSerializer.Serialize(manifest, JsonOpts));
    }

    public Manifest? ReadManifest(string path)
    {
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path));
    }
}

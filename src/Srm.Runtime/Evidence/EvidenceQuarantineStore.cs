using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Srm.Runtime.Evidence;

public enum EvidenceState
{
    Quarantined,
    Promoted,
    Rejected,
}

public class EvidenceRun
{
    [JsonPropertyName("app")]
    public string App { get; set; } = "";

    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = "";

    [JsonPropertyName("quarantined_at")]
    public DateTimeOffset QuarantinedAt { get; set; }

    [JsonPropertyName("state")]
    public EvidenceState State { get; set; } = EvidenceState.Quarantined;

    [JsonPropertyName("file_count")]
    public int FileCount { get; set; }

    [JsonPropertyName("total_size_bytes")]
    public long TotalSizeBytes { get; set; }

    [JsonPropertyName("violations")]
    public List<SanitizeViolation> Violations { get; set; } = new();
}

// outboxの内容を検疫ストアへ隔離し、人間の明示操作(promote/reject)でのみ
// ユーザー指定先へ反映する（DC-013）。スキャン結果や違反検出は記録するだけで、
// 自動的な受け入れ/拒否のゲートにはしない。
public class EvidenceQuarantineStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _root;
    private readonly EvidenceManifestBuilder _manifestBuilder = new();
    private readonly Func<string, string>? _scanFile;

    // scanFileは任意（DC-013のWindows Defenderスキャン、6.7）。CLIからは
    // WindowsDefenderScanner.Scanを渡す。テストでは省略してスキャン無しのまま検証できる。
    public EvidenceQuarantineStore(string? root = null, Func<string, string>? scanFile = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SRM", "evidence");
        _scanFile = scanFile;
    }

    public EvidenceRun Quarantine(string app, string runId, string sourceDir)
    {
        // Quarantine()は管理者権限のsrm run/stopからのみ呼ばれるが、promote/reject/
        // list/showは非管理者のsrm evidenceから呼ぶ設計（DC-013）。既定の%ProgramData%
        // 継承ACLだと新規ファイルにBUILTIN\Usersの書き込み権が付かず、非管理者からの
        // 後続のindex.json更新（Promote/Reject）がAccess Deniedになる（実機で確認済み）。
        // ルートに一度だけ継承可能な書き込み許可を追加し、以降作成される全ファイルへ
        // 伝播させる。
        EnsureUserWritable(_root);

        var runDir = RunDir(app, runId);
        Directory.CreateDirectory(runDir);

        var violations = EvidenceSanitizer.Scan(sourceDir);
        var violatedPaths = violations.Select(v => v.RelativePath).ToHashSet();

        var filesDir = Path.Combine(runDir, "files");
        Directory.CreateDirectory(filesDir);

        long totalSize = 0;
        int fileCount = 0;
        if (Directory.Exists(sourceDir))
        {
            foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var rel = EvidenceSanitizer.GetSafeRelativePath(sourceDir, file);
                if (violatedPaths.Contains(rel))
                    continue; // 違反ファイルは検疫データに取り込まない

                var dest = Path.Combine(filesDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
                totalSize += new FileInfo(file).Length;
                fileCount++;
            }
        }

        var manifest = _manifestBuilder.Build(app, runId, filesDir, _scanFile);
        _manifestBuilder.WriteManifest(manifest, Path.Combine(runDir, "manifest.json"));

        var run = new EvidenceRun
        {
            App = app,
            RunId = runId,
            QuarantinedAt = DateTimeOffset.Now,
            State = EvidenceState.Quarantined,
            FileCount = fileCount,
            TotalSizeBytes = totalSize,
            Violations = violations,
        };
        SaveIndex(run);
        return run;
    }

    public List<EvidenceRun> List(string app)
    {
        var appDir = Path.Combine(_root, app);
        if (!Directory.Exists(appDir)) return new();

        var runs = new List<EvidenceRun>();
        foreach (var dir in Directory.EnumerateDirectories(appDir))
        {
            var run = LoadIndex(app, Path.GetFileName(dir));
            if (run != null) runs.Add(run);
        }
        return runs.OrderByDescending(r => r.QuarantinedAt).ToList();
    }

    public EvidenceRun? Show(string app, string runId) => LoadIndex(app, runId);

    // 検疫からコピー(move ではない、DC-013)。検疫側の控えは監査証跡として残す。
    public void Promote(string app, string runId, string destPath)
    {
        var run = LoadIndex(app, runId)
            ?? throw new InvalidOperationException($"検疫データが見つかりません: {app}/{runId}");

        var filesDir = Path.Combine(RunDir(app, runId), "files");
        Directory.CreateDirectory(destPath);
        if (Directory.Exists(filesDir))
        {
            foreach (var file in Directory.EnumerateFiles(filesDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(filesDir, file);
                var dest = Path.Combine(destPath, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
            }
        }

        run.State = EvidenceState.Promoted;
        SaveIndex(run);
    }

    public void Reject(string app, string runId)
    {
        var run = LoadIndex(app, runId)
            ?? throw new InvalidOperationException($"検疫データが見つかりません: {app}/{runId}");
        run.State = EvidenceState.Rejected;
        SaveIndex(run);
    }

    // Quarantined状態のまま保持期限を過ぎたものだけを削除する。Promoted/Rejectedは
    // 既に人間が判断を下した確定結果なので保持期限の対象外とする（DC-013）。
    public int PurgeExpired(string app, int retentionDays)
    {
        var cutoff = DateTimeOffset.Now.AddDays(-retentionDays);
        int purged = 0;
        foreach (var run in List(app))
        {
            if (run.State != EvidenceState.Quarantined) continue;
            if (run.QuarantinedAt > cutoff) continue;

            var dir = RunDir(app, run.RunId);
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
            purged++;
        }
        return purged;
    }

    // 冪等（既に付与済みでも害はない、AddAccessRuleは同一ルールを正規化してマージする）。
    // NTFS ACL操作自体が失敗しうる環境（ReFS等）を考慮しベストエフォートにする。
    private static void EnsureUserWritable(string root)
    {
        try
        {
            Directory.CreateDirectory(root);
            var info = new DirectoryInfo(root);
            var security = info.GetAccessControl();
            var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            security.AddAccessRule(new FileSystemAccessRule(
                usersSid,
                FileSystemRights.Modify | FileSystemRights.Synchronize,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        catch
        {
            /* ベストエフォート。失敗しても検疫自体は続行する */
        }
    }

    private string RunDir(string app, string runId) => Path.Combine(_root, app, runId);
    private string IndexPath(string app, string runId) => Path.Combine(RunDir(app, runId), "index.json");

    internal void SaveIndex(EvidenceRun run)
    {
        Directory.CreateDirectory(RunDir(run.App, run.RunId));
        File.WriteAllText(IndexPath(run.App, run.RunId), JsonSerializer.Serialize(run, JsonOpts));
    }

    private EvidenceRun? LoadIndex(string app, string runId)
    {
        var path = IndexPath(app, runId);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<EvidenceRun>(File.ReadAllText(path), JsonOpts); }
        catch { return null; }
    }
}

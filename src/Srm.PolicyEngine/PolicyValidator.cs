using Srm.PolicyEngine.Models;

namespace Srm.PolicyEngine;

public class ValidationResult
{
    public bool IsValid => Errors.Count == 0;
    public List<string> Errors { get; } = new();
}

public class PolicyValidator
{
    private static readonly HashSet<string> ValidAccessModes = new(StringComparer.OrdinalIgnoreCase) { "r", "rw" };
    private static readonly HashSet<string> ValidLogLevels = new(StringComparer.OrdinalIgnoreCase) { "debug", "info", "warn", "error" };

    public ValidationResult Validate(PolicyModel policy)
    {
        var result = new ValidationResult();

        if (string.IsNullOrWhiteSpace(policy.Name))
            result.Errors.Add("必須フィールド 'name' が指定されていません。ポリシーファイルに name: <アプリ名> を追加してください。");

        if (policy.Tier is not (1 or 2))
            result.Errors.Add($"'tier' の値が無効です: {policy.Tier}。有効な値は 1 (AppContainer) または 2 (Windows Sandbox) です。");

        if (string.IsNullOrWhiteSpace(policy.Application.Executable))
            result.Errors.Add("必須フィールド 'application.executable' が指定されていません。実行ファイルのパスを指定してください。");

        foreach (var p in policy.Filesystem.AllowPaths)
        {
            if (string.IsNullOrWhiteSpace(p.Path))
            {
                result.Errors.Add("filesystem.allow_paths に空のパスが含まれています。");
                continue;
            }
            if (!ValidAccessModes.Contains(p.Access))
                result.Errors.Add($"filesystem.allow_paths のアクセスモードが無効です: '{p.Access}' (path: {p.Path})。有効な値は 'r' または 'rw' です。");
        }

        if (policy.Process.MaxProcesses < 1)
            result.Errors.Add($"process.max_processes は 1 以上である必要があります: {policy.Process.MaxProcesses}");

        if (!ValidLogLevels.Contains(policy.Logging.Level))
            result.Errors.Add($"logging.level の値が無効です: '{policy.Logging.Level}'。有効な値は debug / info / warn / error です。");

        if (policy.Logging.RetentionDays < 1)
            result.Errors.Add($"logging.retention_days は 1 以上である必要があります: {policy.Logging.RetentionDays}");

        var hasProvision = policy.Provision.Toolchain.Count > 0 || policy.Provision.Steps.Count > 0;
        if (policy.Tier == 1 && hasProvision)
            result.Errors.Add("'provision' は tier: 2 専用です。tier: 1 のポリシーでは指定できません。");

        foreach (var t in policy.Provision.Toolchain)
        {
            if (string.IsNullOrWhiteSpace(t.Name))
                result.Errors.Add("provision.toolchain に name が指定されていないエントリがあります。");
        }

        if (policy.Evidence.RetentionDays < 1)
            result.Errors.Add($"evidence.retention_days は 1 以上である必要があります: {policy.Evidence.RetentionDays}");

        if (policy.Evidence.MaxOutboxBytes is <= 0)
            result.Errors.Add($"evidence.max_outbox_bytes は 1 以上である必要があります（無制限にする場合は指定しないこと）: {policy.Evidence.MaxOutboxBytes}");

        foreach (var server in policy.Mcp.AllowServers)
        {
            if (string.IsNullOrWhiteSpace(server.Name))
                result.Errors.Add("mcp.allow_servers に name が指定されていないエントリがあります。");
            if (string.IsNullOrWhiteSpace(server.Command))
                result.Errors.Add($"mcp.allow_servers のエントリ '{server.Name}' に command が指定されていません。");
            if (server.AllowTools != null && server.AllowTools.Any(string.IsNullOrWhiteSpace))
                result.Errors.Add($"mcp.allow_servers のエントリ '{server.Name}' の allow_tools に空の値が含まれています。");
        }

        return result;
    }
}

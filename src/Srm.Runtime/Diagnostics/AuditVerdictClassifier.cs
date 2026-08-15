using System.Net;
using Srm.PolicyEngine.Models;

namespace Srm.Runtime.Diagnostics;

// resource-access-audit-logging: `srm audit`が観測したファイルパス/接続先を、対象
// ポリシーのfilesystem.allow_paths/network.allow_hostsと突き合わせて「srm runなら
// 許可されていたか(Allowed)、拒否されていたはずか(WouldBlock)」を判定する。判定自体は
// ETW/AppContainer/WFPのいずれにも依存しない純粋なロジックであり、ネイティブAPI呼び出しを
// 含まないため、Windows以外の環境でも単体テストできる（他のRuntimeのETW/ACL/WFPコードとの
// 大きな違い）。
public class AuditVerdictClassifier
{
    private readonly List<string> _normalizedAllowPaths;
    private readonly HashSet<IPAddress> _allowedIps;

    public AuditVerdictClassifier(FilesystemPolicy filesystem, NetworkPolicy network)
    {
        _normalizedAllowPaths = filesystem.AllowPaths
            .Select(p => NormalizePath(p.Path))
            .Where(p => p != null)
            .Select(p => p!)
            .ToList();

        _allowedIps = ResolveAllowHostIps(network.AllowHosts);

        // WfpManager.AddAllowRulesと同じ規約: allow_hostsが1件でもあれば、srm run実行時に
        // ループバックが自動的に許可される（コメント参照）。auditの判定もそれに合わせる。
        if (network.AllowHosts.Count > 0)
        {
            _allowedIps.Add(IPAddress.Loopback);
            _allowedIps.Add(IPAddress.IPv6Loopback);
        }
    }

    public AuditVerdict ClassifyPath(string path)
    {
        var full = NormalizePath(path);
        if (full == null) return AuditVerdict.WouldBlock;

        foreach (var allowed in _normalizedAllowPaths)
        {
            if (full.Equals(allowed, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return AuditVerdict.Allowed;
        }

        return AuditVerdict.WouldBlock;
    }

    public AuditVerdict ClassifyHost(IPAddress remoteAddress) =>
        _allowedIps.Contains(remoteAddress) ? AuditVerdict.Allowed : AuditVerdict.WouldBlock;

    private static string? NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar); }
        catch { return null; }
    }

    // WfpManager.ResolveHost（private static）と同じ解決規則（ワイルドカードは
    // apexドメインのみ解決）をここでも使う。WfpManager側を変更するリスクを避けるため、
    // 同じロジックをこちらに複製している（design.md参照）。
    private static HashSet<IPAddress> ResolveAllowHostIps(List<string> allowHosts)
    {
        var result = new HashSet<IPAddress>();
        foreach (var host in allowHosts)
        {
            var target = host.StartsWith("*.", StringComparison.Ordinal) ? host[2..] : host;
            try
            {
                foreach (var ip in Dns.GetHostAddresses(target))
                    result.Add(ip);
            }
            catch
            {
                // 解決できないホストはベストエフォートでスキップ（WfpManager.ResolveHostと同じ規約）。
            }
        }
        return result;
    }
}

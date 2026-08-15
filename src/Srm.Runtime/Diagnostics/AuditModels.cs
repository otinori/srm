namespace Srm.Runtime.Diagnostics;

// resource-access-audit-logging: `srm audit`が記録する、拒否せず観測したアクセス試行の
// 判定結果。allowedはそのアクセスが対象ポリシーのallow_paths/allow_hostsに合致すること、
// wouldBlockは合致しない（＝もし`srm run`で実行していたら拒否されていたはず）ことを表す。
public enum AuditVerdict
{
    Allowed,
    WouldBlock,
}

public sealed record AuditFileEvent(DateTimeOffset Timestamp, int Pid, string Path, AuditVerdict Verdict);

public sealed record AuditNetworkEvent(DateTimeOffset Timestamp, int Pid, string RemoteAddress, int RemotePort, AuditVerdict Verdict);

// audit実行終了時にログへ書き出す集計結果。allow_paths/allow_hostsをそのまま
// 埋めるための下書き（unique版）としてそのまま使えることを意図している。
public sealed class AuditSummary
{
    public int FileEventCount { get; init; }
    public int NetworkEventCount { get; init; }
    public IReadOnlyList<string> AllowedPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> WouldBlockPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AllowedHosts { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> WouldBlockHosts { get; init; } = Array.Empty<string>();
}

namespace Srm.Runtime.Sandbox;

// channel-d-guest-mcp-bridge: Tier1にはTier2のSandboxRunPaths（MappedFolder前提の
// input/outbox/control）に相当するものが存在しなかった（Tier1はVM境界が無く、
// AppContainerのACL付与だけで済んでいたため）。Channel Dのcontrolフォルダ
// （SandboxControlChannelをTier1でも再利用する。実装はTier2専用の処理を一切
// 含まない純粋なファイルI/Oのため、そのまま流用できる）専用に、Tier2の
// runIdに相当する概念を持たない最小限のパス規約を追加する（Tier1は
// `srm-job-{app}`同様アプリ名単位で一意という既存の前提に揃える）。
public static class Tier1ControlPaths
{
    public static string ControlDir(string appName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SRM", "run", appName, "control");
}

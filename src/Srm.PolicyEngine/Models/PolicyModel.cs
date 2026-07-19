using YamlDotNet.Serialization;

namespace Srm.PolicyEngine.Models;

public class PolicyModel
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "description")]
    public string Description { get; set; } = "";

    [YamlMember(Alias = "tier")]
    public int Tier { get; set; } = 1;

    [YamlMember(Alias = "application")]
    public ApplicationConfig Application { get; set; } = new();

    [YamlMember(Alias = "variables")]
    public Dictionary<string, string> Variables { get; set; } = new();

    [YamlMember(Alias = "filesystem")]
    public FilesystemPolicy Filesystem { get; set; } = new();

    [YamlMember(Alias = "network")]
    public NetworkPolicy Network { get; set; } = new();

    [YamlMember(Alias = "process")]
    public ProcessPolicy Process { get; set; } = new();

    [YamlMember(Alias = "logging")]
    public LoggingPolicy Logging { get; set; } = new();

    [YamlMember(Alias = "provision")]
    public ProvisionPolicy Provision { get; set; } = new();

    [YamlMember(Alias = "evidence")]
    public EvidencePolicy Evidence { get; set; } = new();

    [YamlMember(Alias = "tier2")]
    public Tier2Policy Tier2 { get; set; } = new();

    [YamlMember(Alias = "mcp")]
    public McpPolicy Mcp { get; set; } = new();
}

public class ApplicationConfig
{
    [YamlMember(Alias = "executable")]
    public string Executable { get; set; } = "";

    [YamlMember(Alias = "arguments")]
    public string Arguments { get; set; } = "";

    [YamlMember(Alias = "working_directory")]
    public string WorkingDirectory { get; set; } = "";
}

public class FilesystemPolicy
{
    [YamlMember(Alias = "allow_paths")]
    public List<AllowedPath> AllowPaths { get; set; } = new();
}

public class AllowedPath
{
    [YamlMember(Alias = "path")]
    public string Path { get; set; } = "";

    [YamlMember(Alias = "access")]
    public string Access { get; set; } = "r";
}

public class NetworkPolicy
{
    [YamlMember(Alias = "allow_hosts")]
    public List<string> AllowHosts { get; set; } = new();
}

public class ProcessPolicy
{
    [YamlMember(Alias = "allow_child_processes")]
    public bool AllowChildProcesses { get; set; } = true;

    [YamlMember(Alias = "max_processes")]
    public int MaxProcesses { get; set; } = 10;
}

public class LoggingPolicy
{
    [YamlMember(Alias = "level")]
    public string Level { get; set; } = "info";

    [YamlMember(Alias = "retention_days")]
    public int RetentionDays { get; set; } = 7;
}

// Tier2専用（DC-012）。ホストが事前キャッシュしたポータブルツールチェーンをゲストに
// 展開する手順を宣言する。バージョン解決・ダウンロードはsrmの責務にしない
// （DC-005と同じく既存エコシステムに委ねる）。
public class ProvisionPolicy
{
    [YamlMember(Alias = "toolchain")]
    public List<ToolchainEntry> Toolchain { get; set; } = new();

    [YamlMember(Alias = "steps")]
    public List<string> Steps { get; set; } = new();

    // 既定オフライン（DC-012）。trueにする場合はexecutionフェーズと同じ
    // network.allow_hosts の審査下に置く。
    [YamlMember(Alias = "network")]
    public bool Network { get; set; } = false;
}

public class ToolchainEntry
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "version")]
    public string Version { get; set; } = "";
}

// Tier2専用（DC-013）。outboxの検疫〜昇格パイプラインの設定。
public class EvidencePolicy
{
    [YamlMember(Alias = "source_path")]
    public string SourcePath { get; set; } = "";

    [YamlMember(Alias = "retention_days")]
    public int RetentionDays { get; set; } = 7;

    // outboxの合計サイズがこれを超えたら実行中でも早期終了させる（DC-013のクォータ対策）。
    // null/0は「無制限」を意味する既定値。指定した場合のみホスト側の監視プロセスが起動する。
    [YamlMember(Alias = "max_outbox_bytes")]
    public long? MaxOutboxBytes { get; set; }
}

// Tier2専用（tier2-guest-network-egress）。DC-010は常にネスト先をAppContainerで
// ラップする設計だったが、DC-016でAppContainerトークンとBun（Claude Code）が
// 非互換と判明したため、opt-out（app_container: false）を追加する。falseの場合、
// ゲスト内でAppContainerLauncherを経由せず、WFPの識別子もPackageSidではなく
// UserSid/AppPathになる（design.md/proposal.md参照）。fs/プロセスの隔離保証が
// AppContainer相当より弱まる（restricted-account-app-isolation実装前はVM境界+
// ネットワーク遮断のみ）ことに注意。
public class Tier2Policy
{
    [YamlMember(Alias = "app_container")]
    public bool AppContainer { get; set; } = true;
}

// channel-d-guest-mcp-bridge: サンドボックス内エージェントが呼び出せるホスト側MCP
// サーバーの許可リスト。network.allow_hosts/filesystem.allow_pathsと同じ
// 「既定拒否・明示許可」の思想（design.md参照）。空（既定）の場合はChannel D自体を
// 起動しない（tasks.md 1.1/design.md Decision「新しいIPC機構は作らない」を参照）。
public class McpPolicy
{
    [YamlMember(Alias = "allow_servers")]
    public List<McpServerEntry> AllowServers { get; set; } = new();
}

public class McpServerEntry
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "command")]
    public string Command { get; set; } = "";

    [YamlMember(Alias = "args")]
    public string Args { get; set; } = "";

    // 省略時はこのサーバーの全ツールを許可する（design.md「allow_tools省略時は
    // サーバー単位の許可に留める」）。
    [YamlMember(Alias = "allow_tools")]
    public List<string>? AllowTools { get; set; }
}

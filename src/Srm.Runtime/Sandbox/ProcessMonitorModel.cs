using System.Text.Json.Serialization;

namespace Srm.Runtime.Sandbox;

// tier2-channel-c-mapped-folder: tier2-process-monitor capability。design.md
// Decision 5の通り、履歴を蓄積せず最新スナップショット1つだけをmonitor-result.json
// へ上書きし続ける（diag-request/resultと同じ蓄積しないファイルペアの方針だが、
// 診断チャネルの「1回要求・1回応答」とは異なり、1回の要求で継続的に上書きし
// 続ける点が違う）。時系列が必要な場合はホスト側が任意の頻度でポーリングして
// 自前で構築する。対象PIDは要求に含めず、既存のJob Object配下のプロセスツリー
// （focus-request/scenarioと同じ`allowedPids`解決）をそのまま使う。
public class ProcessMonitorRequestModel : IHasRequestId
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = "";

    // スナップショットの更新間隔（ミリ秒）。srm diagのSampleWindowMsと違い、
    // 単発の前後差分ではなく継続的な上書き間隔として使う。
    [JsonPropertyName("intervalMs")]
    public int IntervalMs { get; set; } = 1000;
}

public class ProcessSnapshotModel
{
    [JsonPropertyName("processId")]
    public int ProcessId { get; set; }

    // 累積値（DiagResultModelの秒あたりレートと異なり前後差分を取らない）。
    // ホスト側が連続する2つのスナップショットを比較すればレートを導出できる。
    [JsonPropertyName("totalProcessorTimeMs")]
    public double TotalProcessorTimeMs { get; set; }

    [JsonPropertyName("threadCount")]
    public int ThreadCount { get; set; }

    [JsonPropertyName("handleCount")]
    public int HandleCount { get; set; }

    [JsonPropertyName("workingSetBytes")]
    public long WorkingSetBytes { get; set; }
}

public class ProcessMonitorResultModel : IHasRequestId
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = "";

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("timestampUtc")]
    public DateTime TimestampUtc { get; set; }

    // Job Object配下の生存プロセスのみを含む（解決できない/消失したPIDは
    // 一覧から静かに除外する。DiagCollectorの単一PID失敗とは異なり、
    // プロセスツリー全体のうち一部が消失しても致命的エラーにはしない）。
    [JsonPropertyName("processes")]
    public List<ProcessSnapshotModel> Processes { get; set; } = new();
}

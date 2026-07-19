using System.Text.Json.Serialization;

namespace Srm.Runtime.Sandbox;

// DC-016（claude-code CLIをAppContainer内で-p実行するとカーネルモードCPUを消費し
// 続ける未解決バグ）の実機再調査を、都度Procmon/Get-Counterを手動操作せずに行える
// ようにするための軽量診断プロトコル。Tier2はfocus-request.json/focus-result.jsonと
// 同じ「固定パス2ファイルを都度上書きする」方式でゲスト内`--nested`とやり取りする。
// Tier1は同一ホスト上の直接プロセスのため、このモデルは使うがファイル経由の
// 往復はせず、ホスト側から`DiagCollector`を直接呼び出す。
public class DiagRequestModel
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = "";

    // サンプリングに使う経過時間（ミリ秒）。CPU時間・ページフォールト数を
    // この間隔の前後で採取し、差分から秒あたりのレートを計算する。
    [JsonPropertyName("sampleWindowMs")]
    public int SampleWindowMs { get; set; } = 1000;
}

public class DiagResultModel
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = "";

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("processId")]
    public int ProcessId { get; set; }

    [JsonPropertyName("sampleWindowMs")]
    public int SampleWindowMs { get; set; }

    [JsonPropertyName("userTimePercent")]
    public double UserTimePercent { get; set; }

    [JsonPropertyName("kernelTimePercent")]
    public double KernelTimePercent { get; set; }

    [JsonPropertyName("pageFaultsPerSec")]
    public double PageFaultsPerSec { get; set; }

    [JsonPropertyName("threadCount")]
    public int ThreadCount { get; set; }

    [JsonPropertyName("handleCount")]
    public int HandleCount { get; set; }
}

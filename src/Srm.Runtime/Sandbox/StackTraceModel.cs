using System.Text.Json.Serialization;

namespace Srm.Runtime.Sandbox;

// DC-021のreview_trigger: `srm diag --stacktrace`（ETWカーネルCPUスタック
// サンプリング）はTier1のみ対応で、Tier2は「ゲスト内の別カーネルのため
// ホストからのETWセッションでは採取できない」という理由で明示的に非対応
// だった。ETWセッションはサンプリング対象と同じカーネル上でしか動かせないため、
// Tier2ではゲスト内`--nested`自身がセッションを開始する必要がある。tier2-
// channel-c-mapped-folderの汎用request/resultプリミティブ（kind="stacktrace"）
// を使い、ホストが要求→ゲストが`KernelCpuStackSampler`（Srm.Diagnostics.Kernel、
// Tier1と全く同じ実装）を呼び出して採取→結果を返す、diag-request/resultと
// 同じ構造の往復にする。
public class StackTraceRequestModel : IHasRequestId
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = "";

    [JsonPropertyName("durationMs")]
    public int DurationMs { get; set; } = 1000;

    [JsonPropertyName("top")]
    public int Top { get; set; } = 20;
}

public class StackTraceFrameModel
{
    [JsonPropertyName("frame")]
    public string Frame { get; set; } = "";

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("percent")]
    public double Percent { get; set; }
}

public class NamedPipeSampleModel
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("count")]
    public int Count { get; set; }
}

public class StackTraceResultModel : IHasRequestId
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = "";

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("processId")]
    public int ProcessId { get; set; }

    [JsonPropertyName("totalSamples")]
    public int TotalSamples { get; set; }

    [JsonPropertyName("topFrames")]
    public List<StackTraceFrameModel> TopFrames { get; set; } = new();

    [JsonPropertyName("namedPipesCreated")]
    public List<NamedPipeSampleModel> NamedPipesCreated { get; set; } = new();
}

using System.Text.Json.Serialization;

namespace Srm.Runtime.Sandbox;

// チャネルB（オートパイロット、DC-017）: 個々のキー操作を都度やり取りするのではなく、
// 手順の並びをまとめて1回投入し、ゲスト内`--nested`が自律的に最後まで実行する。
public class ScenarioModel
{
    [JsonPropertyName("steps")]
    public List<ScenarioStep> Steps { get; set; } = new();
}

public class ScenarioStep
{
    // "send_key" | "send_mouse" | "screenshot"
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("x")]
    public int? X { get; set; }

    [JsonPropertyName("y")]
    public int? Y { get; set; }

    // screenshotステップの成果物ファイル名のヒント（省略時は連番のみ）
    [JsonPropertyName("label")]
    public string? Label { get; set; }
}

public class ScenarioResultModel
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("steps")]
    public List<ScenarioStepResult> Steps { get; set; } = new();

    [JsonPropertyName("completed_at")]
    public DateTimeOffset CompletedAt { get; set; }
}

public class ScenarioStepResult
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    // outbox内の相対パス（screenshotステップのみ。evidence検疫パイプライン経由で
    // ホスト側から参照できる）
    [JsonPropertyName("artifact_relative_path")]
    public string? ArtifactRelativePath { get; set; }
}

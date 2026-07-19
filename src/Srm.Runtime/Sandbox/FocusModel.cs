using System.Text.Json.Serialization;

namespace Srm.Runtime.Sandbox;

// Tier2チャネルA（DC-017）が、実際のキー/マウスイベント送信の直前にゲスト内
// `--nested`へ「対象アプリへフォーカスを設定してほしい」と依頼するための
// 軽量プロトコル。scenario.json/scenario-result.jsonと同じ「固定パス2ファイルを
// 都度上書きする」方式を踏襲し、DC-017が却下した1コマンド1ファイルのキュー
// （alt-percommand-file-queue）のようにファイルが蓄積することはない。
public class FocusRequestModel
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = "";

    [JsonPropertyName("app")]
    public string App { get; set; } = "";
}

public class FocusResultModel
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = "";

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

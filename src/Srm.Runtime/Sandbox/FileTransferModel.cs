using System.Text.Json.Serialization;

namespace Srm.Runtime.Sandbox;

// tier2-channel-c-mapped-folder: tier2-file-transfer capability。design.md
// Decision 3の通り、control/file-transfer-request.jsonにはメタデータのみを
// 載せ、実体ファイルは既存のInput（ホスト→ゲスト）/Outbox（ゲスト→ホスト）
// マップフォルダ配下のtransfer/<requestId>/サブフォルダで運ぶ。
public enum FileTransferDirection
{
    HostToGuest,
    GuestToHost,
}

public class FileTransferRequestModel : IHasRequestId
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = "";

    [JsonPropertyName("direction")]
    public FileTransferDirection Direction { get; set; }

    // HostToGuest: 転送先（ゲスト内の絶対パス、policy.filesystem.allow_paths配下で
    // なければならない）。GuestToHost: 転送元（同じくallow_paths配下でなければ
    // ならない）。
    [JsonPropertyName("guestPath")]
    public string GuestPath { get; set; } = "";

    // Input/Outboxのtransfer/<requestId>/配下に置く実体ファイルの名前
    // （ディレクトリ区切りを含まないファイル名のみ。パストラバーサル防止のため
    // 検証時にPath.GetFileName(fileName)==fileNameを要求する）。
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = "";
}

public class FileTransferResultModel : IHasRequestId
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = "";

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    // successがfalseの場合の理由（allow_paths範囲外・パストラバーサル・
    // 転送元ファイル不在等）。
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

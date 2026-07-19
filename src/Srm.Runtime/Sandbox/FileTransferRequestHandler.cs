using Srm.PolicyEngine.Models;

namespace Srm.Runtime.Sandbox;

// tier2-channel-c-mapped-folder: ゲスト内`--nested`がfile-transfer-request.jsonを
// 検出したときに呼ぶ、直接テスト可能な本体ロジック（FocusRequestHandlerと同じ
// パターン）。request.GuestPathは方向に関わらず「allow_pathsの範囲内でなければ
// ならないゲスト側の場所」を表す（HostToGuestでは転送先、GuestToHostでは転送元）。
public class FileTransferRequestHandler
{
    public FileTransferResultModel Handle(PolicyModel policy, string guestRoot, FileTransferRequestModel request)
    {
        if (!PathAllowlist.IsBareFileName(request.FileName))
            return Fail(request, $"不正なファイル名です（ディレクトリ区切りを含めることはできません）: {request.FileName}");

        var allowPaths = policy.Filesystem.AllowPaths.Select(p => p.Path);
        if (!PathAllowlist.IsWithinAllowedPaths(request.GuestPath, allowPaths))
            return Fail(request, $"filesystem.allow_pathsの範囲外です: {request.GuestPath}");

        try
        {
            if (request.Direction == FileTransferDirection.HostToGuest)
            {
                var sourcePath = Path.Combine(SandboxConfigGenerator.GuestInputDir(guestRoot), "transfer", request.RequestId, request.FileName);
                if (!File.Exists(sourcePath))
                    return Fail(request, $"転送元ファイルが見つかりません（ホスト側でtransferステージングに失敗した可能性があります）: {sourcePath}");

                var destDir = Path.GetDirectoryName(request.GuestPath);
                if (!string.IsNullOrEmpty(destDir))
                    Directory.CreateDirectory(destDir);
                File.Copy(sourcePath, request.GuestPath, overwrite: true);
            }
            else
            {
                if (!File.Exists(request.GuestPath))
                    return Fail(request, $"転送元ファイルが見つかりません: {request.GuestPath}");

                var destDir = Path.Combine(SandboxConfigGenerator.GuestOutboxDir(guestRoot), "transfer", request.RequestId);
                Directory.CreateDirectory(destDir);
                File.Copy(request.GuestPath, Path.Combine(destDir, request.FileName), overwrite: true);
            }
        }
        catch (Exception ex)
        {
            return Fail(request, $"ファイル転送に失敗しました: {ex.Message}");
        }

        return new FileTransferResultModel { RequestId = request.RequestId, Success = true };
    }

    private static FileTransferResultModel Fail(FileTransferRequestModel request, string reason) =>
        new() { RequestId = request.RequestId, Success = false, Reason = reason };
}

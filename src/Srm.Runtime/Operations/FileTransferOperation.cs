using Srm.Runtime.Sandbox;

namespace Srm.Runtime.Operations;

// tier2-channel-c-mapped-folder: tier2-file-transferのTier1/Tier2共通の入口
// （DiagOperationと同じくConsole出力・Environment.Exitを行わない）。
// design.mdの通りTier2（Windows Sandbox、VM境界）専用の機能で、Tier1（同一OS上の
// AppContainer）はホスト側から対象パスへ直接ファイル操作すれば足りるため対象外。
public class FileTransferOperation
{
    private static readonly TimeSpan ResultTimeout = TimeSpan.FromSeconds(30);

    // ホスト→ゲスト方向: hostSourcePathをInput\transfer\<requestId>\へコピーして
    // ステージングし、ゲスト内へguestDestPathへ配置するよう要求する。
    public FileTransferResultModel Put(string appName, string hostSourcePath, string guestDestPath)
    {
        var app = FindTier2App(appName);

        if (!File.Exists(hostSourcePath))
            throw new SrmOperationException($"転送元ファイルが見つかりません: {hostSourcePath}", 1);

        var fileName = Path.GetFileName(hostSourcePath);
        if (!PathAllowlist.IsBareFileName(fileName))
            throw new SrmOperationException($"転送元ファイル名が不正です: {hostSourcePath}", 1);

        if (string.IsNullOrWhiteSpace(app.InputDir))
            throw new SrmOperationException($"{appName} のinput_dirが記録されていません", 2);

        var requestId = Guid.NewGuid().ToString();
        var stagingDir = Path.Combine(app.InputDir, "transfer", requestId);
        Directory.CreateDirectory(stagingDir);
        File.Copy(hostSourcePath, Path.Combine(stagingDir, fileName));

        var request = new FileTransferRequestModel
        {
            RequestId = requestId,
            Direction = FileTransferDirection.HostToGuest,
            GuestPath = guestDestPath,
            FileName = fileName,
        };

        return SendAndWait(app, request);
    }

    // ゲスト→ホスト方向: ゲスト内guestSourcePathをOutbox\transfer\<requestId>\へ
    // 配置するよう要求する。design.md Decision 4の通り、この時点ではユーザーの
    // 作業ディレクトリへ自動反映しない — DC-013の既存の検疫（srm stop時のoutbox
    // スキャン）→`srm evidence promote`という二段構えのゲートをそのまま経由する。
    public FileTransferResultModel Get(string appName, string guestSourcePath)
    {
        var app = FindTier2App(appName);

        var fileName = Path.GetFileName(guestSourcePath);
        if (!PathAllowlist.IsBareFileName(fileName))
            throw new SrmOperationException($"転送元パスが不正です: {guestSourcePath}", 1);

        var request = new FileTransferRequestModel
        {
            RequestId = Guid.NewGuid().ToString(),
            Direction = FileTransferDirection.GuestToHost,
            GuestPath = guestSourcePath,
            FileName = fileName,
        };

        return SendAndWait(app, request);
    }

    private static FileTransferResultModel SendAndWait(RunningApp app, FileTransferRequestModel request)
    {
        var control = new SandboxControlChannel(app.ControlDir!);
        control.WriteRequest("file-transfer", request);

        return control.WaitForResult<FileTransferResultModel>("file-transfer", request.RequestId, ResultTimeout)
            ?? throw new SrmOperationException($"ゲスト内でのファイル転送がタイムアウトしました: {app.Name}", 5);
    }

    private static RunningApp FindTier2App(string appName)
    {
        var app = RunningAppRegistry.Find(appName)
            ?? throw new SrmOperationException(
                $"実行中のアプリが見つかりません: {appName}\nsrm list で実行中のアプリを確認してください", 1);

        if (app.Tier != 2)
            throw new SrmOperationException(
                $"tier2-file-transferはTier2専用です: {appName}\n" +
                "Tier1（AppContainer、ホストと同一OS）はVM境界が無いため、ホスト側から対象パスへ直接ファイル操作してください。", 1);

        if (string.IsNullOrWhiteSpace(app.ControlDir))
            throw new SrmOperationException($"{appName} のcontrol_dirが記録されていません", 2);

        return app;
    }
}

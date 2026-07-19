using Srm.Runtime.Sandbox;

namespace Srm.Runtime.Operations;

// tier2-channel-c-mapped-folder: tier2-process-monitorのTier2専用入口
// （FileTransferOperationと同じくConsole出力・Environment.Exitを行わない）。
// Tier1（AppContainer、ホストと同一OS）はVM境界が無く対象プロセスがホストから
// 直接見えるため、既存のsrm diag（DiagOperation）で直接サンプリングすれば足り、
// この経路は対象外とする。
public class ProcessMonitorOperation
{
    // 新しいスナップショットが書き込まれるたびに1件ずつ返す。requestIdを
    // 一度だけ発行してゲスト側の継続ループを起動し、TimestampUtcの変化で
    // 「新しいスナップショットが来た」ことを検出する（design.md Decision 5の
    // 「履歴を蓄積しない」方針を守りつつ、ホスト側だけで簡易的な時系列を作る）。
    public IReadOnlyList<ProcessMonitorResultModel> Watch(string appName, int intervalMs, int count)
    {
        var app = RunningAppRegistry.Find(appName)
            ?? throw new SrmOperationException(
                $"実行中のアプリが見つかりません: {appName}\nsrm list で実行中のアプリを確認してください", 1);

        if (app.Tier != 2)
            throw new SrmOperationException(
                $"tier2-process-monitorはTier2専用です: {appName}\n" +
                "Tier1（AppContainer、ホストと同一OS）は既存のsrm diagで直接サンプリングしてください。", 1);

        if (string.IsNullOrWhiteSpace(app.ControlDir))
            throw new SrmOperationException($"{appName} のcontrol_dirが記録されていません", 2);

        var control = new SandboxControlChannel(app.ControlDir);
        var requestId = Guid.NewGuid().ToString();
        control.WriteRequest("process-monitor", new ProcessMonitorRequestModel { RequestId = requestId, IntervalMs = intervalMs });

        var results = new List<ProcessMonitorResultModel>();
        DateTime? lastTimestampSeen = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds((long)intervalMs * (count + 3) + 5000);

        while (results.Count < count && DateTime.UtcNow < deadline)
        {
            var result = control.TryReadResult<ProcessMonitorResultModel>("process-monitor");
            if (result != null && result.RequestId == requestId && result.TimestampUtc != lastTimestampSeen)
            {
                lastTimestampSeen = result.TimestampUtc;
                results.Add(result);
            }

            Thread.Sleep(Math.Min(intervalMs, 250));
        }

        if (results.Count == 0)
            throw new SrmOperationException($"ゲスト内でのプロセス監視がタイムアウトしました: {appName}", 5);

        return results;
    }
}

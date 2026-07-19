using Srm.Runtime.Diagnostics;
using Srm.Runtime.Sandbox;

namespace Srm.Runtime.Operations;

// srm diag のTier1/Tier2共通の入口。DC-016の実機再調査を、都度Procmon/Get-Counterを
// 手動操作せずに行えるようにする診断コマンドの本体（Console出力・Environment.Exitを
// 行わない。RunOperation/StopOperationと同じ理由でCLI/MCPどちらからも再利用できる形にする）。
//
// Tier1（AppContainer、ホストと同一セッション）は対象プロセスがホストから直接
// 見えるため、ゲスト↔ホストの往復は不要でDiagCollectorをその場で直接呼び出す。
// Tier2（Windows Sandbox）は対象プロセスが別VM上のため、SandboxControlChannel
// （tier2-channel-a-focus-guaranteeのfocus-request/resultと同じ固定パス2ファイル
// 上書き方式）でゲスト内`--nested`へ委譲する。
public class DiagOperation
{
    private static readonly TimeSpan ResultTimeoutBuffer = TimeSpan.FromSeconds(5);

    public DiagResultModel Execute(string appName, int sampleWindowMs = 1000)
    {
        var app = RunningAppRegistry.Find(appName)
            ?? throw new SrmOperationException(
                $"実行中のアプリが見つかりません: {appName}\nsrm list で実行中のアプリを確認してください", 1);

        var request = new DiagRequestModel { RequestId = Guid.NewGuid().ToString(), SampleWindowMs = sampleWindowMs };

        if (app.Tier != 2)
            return new DiagCollector().Handle(app.Pid, request);

        if (string.IsNullOrWhiteSpace(app.ControlDir))
            throw new SrmOperationException($"{appName} のcontrol_dirが記録されていません", 2);

        var control = new SandboxControlChannel(app.ControlDir);
        control.WriteDiagRequest(request);

        var timeout = TimeSpan.FromMilliseconds(sampleWindowMs) + ResultTimeoutBuffer;
        return control.WaitForDiagResult(request.RequestId, timeout)
            ?? throw new SrmOperationException($"ゲスト内での診断サンプリングがタイムアウトしました: {appName}", 5);
    }
}

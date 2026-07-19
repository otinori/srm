using System.Diagnostics;
using Srm.PolicyEngine.Models;

namespace Srm.Runtime.Sandbox;

public class SandboxRunResult
{
    public required Process HostProcess { get; init; }
    public required SandboxRunPaths Paths { get; init; }
    public required SandboxControlChannel Control { get; init; }
}

// .wsb生成→ACL事前付与→WindowsSandbox.exe起動→ready.signal待機、のオーケストレーション
// （DC-010/DC-011）。実際のVM起動・ゲスト内動作の検証はWindows実機でのみ可能。
public class SandboxLauncher
{
    private readonly SandboxAclPreparer _aclPreparer = new();

    public SandboxRunResult Launch(PolicyModel policy, string policyPath, string runId, string toolingDir, TimeSpan readyTimeout)
    {
        var paths = new SandboxRunPaths(policy.Name, runId);
        paths.EnsureCreated();

        _aclPreparer.Prepare(policy, paths);

        // ゲスト内の --nested は起動直後にpolicy.yamlを読む。WindowsSandbox.exeの起動
        // （ひいてはLogonCommandの発火）より後にコピーすると、ゲスト側がまだ存在しない
        // ファイルを読もうとして即座に失敗する（実機で再現・確認済み）。必ず起動前に置く。
        File.Copy(policyPath, Path.Combine(paths.Control, "policy.yaml"), overwrite: true);

        var control = new SandboxControlChannel(paths.Control);

        const string guestRoot = @"C:\srm";
        var guestSrmExe = $"{SandboxConfigGenerator.GuestToolingDir(guestRoot)}\\srm.exe";
        var nestedCommand =
            $"{guestSrmExe} run {policy.Name} --nested --control-dir {SandboxConfigGenerator.GuestControlDir(guestRoot)}";

        // LogonCommandで起動されたプロセスには有効なコンソール(標準入出力ハンドル)が
        // 無く、出力先を明示的にリダイレクトしないとnested srm.exeがControl.SignalReady()
        // に到達する前に失敗する（実機で再現・確認済み。cmd.exe /c 経由でファイルへ
        // リダイレクトすると成功する）。ログはcontrolフォルダに書き、ホスト側からも
        // 障害調査に使えるようにする。
        var logonCommand =
            $"cmd.exe /c \"{nestedCommand} > {SandboxConfigGenerator.GuestControlDir(guestRoot)}\\nested.log 2>&1\"";

        var spec = new SandboxLaunchSpec
        {
            ToolingHostDir = toolingDir,
            InputHostDir = paths.Input,
            OutboxHostDir = paths.Outbox,
            ControlHostDir = paths.Control,
            // provision.toolchainが指定されている場合のみキャッシュ全体をROマウントする
            // （DC-012）。存在確認自体はRunTier2側で事前に行う。
            ProvisionHostDir = policy.Provision.Toolchain.Count > 0 ? ToolchainCache.Root : null,
            GuestRoot = guestRoot,
            // .wsbレベルではVM単位でしかon/offを切れないため、allow_hostsか
            // provision.networkのどちらかが必要ならNetworkingを有効にする。実際の
            // ホスト/ドメイン単位のallow/denyは、Hyper-V Firewallが使えないと
            // 実機PoCで判明したため（DC-015、DC-011改訂）ゲスト内nested側の
            // WfpManagerで行う（RunCommand.RunNested参照）。
            NetworkingEnabled = policy.Network.AllowHosts.Count > 0 || policy.Provision.Network,
            LogonCommand = logonCommand,
        };

        var wsbPath = Path.Combine(paths.RunRoot, "sandbox.wsb");
        SandboxConfigGenerator.WriteToFile(spec, wsbPath);

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "WindowsSandbox.exe",
            Arguments = $"\"{wsbPath}\"",
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("WindowsSandbox.exe の起動に失敗しました");

        control.WaitForReady(readyTimeout);

        return new SandboxRunResult { HostProcess = process, Paths = paths, Control = control };
    }

    // srm stop のTier2対応: stop.signal→stopped.signalの正常終了フローを試み、
    // タイムアウト時はVMホストプロセスを強制終了する。
    public void Stop(SandboxRunResult result, TimeSpan gracefulTimeout)
    {
        result.Control.SignalStop();
        if (!result.Control.WaitForStopped(gracefulTimeout))
        {
            try { result.HostProcess.Kill(entireProcessTree: true); }
            catch { /* ベストエフォート */ }
        }
    }
}

using Srm.Runtime.Native;

namespace Srm.Runtime.Interaction;

// Tier1（AppContainer）向け: RunningAppRegistryが記録したJob Object名から、
// 現在生きているメンバープロセスのPID集合と、それらが所有する可視ウィンドウを解決する。
// ホストと対象アプリは同一Windowsセッション上で動作するため、ブリッジ機構は不要
// （DC-017 チャネルA）。ゲスト内（Tier2、--nested）で対象アプリを直接起動する場合も
// 同一セッション内での話になるため、このクラスをそのまま再利用できる。
public class Tier1WindowResolver
{
    public HashSet<int> GetAllowedPids(RunningApp app)
    {
        if (string.IsNullOrWhiteSpace(app.JobName))
            return new HashSet<int> { app.Pid };

        if (!JobObjectManager.TryGetProcessIds(app.JobName, out var pids))
            return new HashSet<int> { app.Pid }; // Job Objectが既に無い場合、最低限トップレベルPIDだけは許可する

        var set = pids.ToHashSet();
        set.Add(app.Pid);
        return set;
    }

    // 許可されたPID集合に属する可視ウィンドウをZオーダー順（EnumWindowsの列挙順、
    // 手前のウィンドウが先）に返す。複数見つかった場合、呼び出し元は先頭
    // （最も手前にあるもの）を使う想定。
    public List<IntPtr> ResolveCandidateWindows(HashSet<int> allowedPids)
    {
        var result = new List<IntPtr>();
        User32Native.EnumWindows((hwnd, _) =>
        {
            User32Native.GetWindowThreadProcessId(hwnd, out var ownerPid);
            if (allowedPids.Contains((int)ownerPid) && User32Native.IsWindowVisible(hwnd))
                result.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return result;
    }
}

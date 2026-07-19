using System.Text;
using Srm.Runtime.Native;

namespace Srm.Runtime.Interaction;

public record WindowGuardRequest(
    IntPtr Hwnd,
    IReadOnlySet<int> AllowedPids,
    string? ExpectedExecutablePath = null,
    // Tier2はWindowsSandbox.exe自体が実際にウィンドウを持つのか、別の子プロセス
    // （WindowsSandboxClient.exe等）が持つのか実機未検証（DC-017 review_trigger）。
    // 完全一致まで要求すると実機で誤って全拒否になりかねないため、ディレクトリ・
    // ファイル名部分一致のゆるい検証も選べるようにする。
    string? ExpectedExecutableDirectory = null,
    string? ExpectedExecutableNameContains = null,
    string? ExpectedClassName = null,
    (int X, int Y)? ScreenPointForMouse = null,
    bool RequireForeground = true);

public record WindowGuardResult(bool Allowed, string? Reason)
{
    public static WindowGuardResult Allow() => new(true, null);
    public static WindowGuardResult Deny(string reason) => new(false, reason);
}

// チャネルA（対話的ライブ制御、ホスト側=Srm.Mcp）・チャネルBのゲスト内実行（Srm.Cli
// --nested）の両方が、SendInput/PrintWindowの直前に毎回通す再検証ロジック
// （DC-017 decision 4、fail-closed）。HWNDはOSに再利用されるため、呼び出し元は
// 結果をキャッシュせず毎回このクラスを通すこと。対象を取り違えるとサンドボックス外
// （Tier1ならホストの他ウィンドウ、Tier2ならゲスト内の他プロセス）へ入力が漏れる
// リスクがあるため、判定に迷ったら必ずDenyを返す。
public class WindowGuard
{
    public WindowGuardResult Authorize(WindowGuardRequest request)
    {
        var hwnd = request.Hwnd;
        if (hwnd == IntPtr.Zero)
            return WindowGuardResult.Deny("ウィンドウハンドルが無効です");

        // 1. 所有PIDが許可されたプロセス集合に属しているか
        User32Native.GetWindowThreadProcessId(hwnd, out var ownerPid);
        if (ownerPid == 0 || !request.AllowedPids.Contains((int)ownerPid))
            return WindowGuardResult.Deny(
                $"対象ウィンドウの所有プロセス(pid={ownerPid})が許可されたプロセス集合に含まれていません");

        // 2. 実行ファイルパス（指定されている場合のみ。例: Tier2はWindowsSandbox.exeを期待）
        if (request.ExpectedExecutablePath != null ||
            request.ExpectedExecutableDirectory != null ||
            request.ExpectedExecutableNameContains != null)
        {
            var actualPath = TryGetExecutablePath(ownerPid);
            if (actualPath == null)
                return WindowGuardResult.Deny("所有プロセスの実行ファイルパスを取得できませんでした");

            if (request.ExpectedExecutablePath != null &&
                !actualPath.Equals(request.ExpectedExecutablePath, StringComparison.OrdinalIgnoreCase))
                return WindowGuardResult.Deny(
                    $"所有プロセスの実行ファイルパスが期待値と一致しません（期待: {request.ExpectedExecutablePath}, 実際: {actualPath}）");

            if (request.ExpectedExecutableDirectory != null &&
                !(Path.GetDirectoryName(actualPath)?.Equals(request.ExpectedExecutableDirectory, StringComparison.OrdinalIgnoreCase) ?? false))
                return WindowGuardResult.Deny(
                    $"所有プロセスの実行ファイルが期待するディレクトリ配下にありません（期待: {request.ExpectedExecutableDirectory}, 実際: {actualPath}）");

            if (request.ExpectedExecutableNameContains != null &&
                Path.GetFileName(actualPath).IndexOf(request.ExpectedExecutableNameContains, StringComparison.OrdinalIgnoreCase) < 0)
                return WindowGuardResult.Deny(
                    $"所有プロセスの実行ファイル名が期待する文字列を含みません（期待に含む: {request.ExpectedExecutableNameContains}, 実際: {actualPath}）");
        }

        // 3. ウィンドウクラス名（指定されている場合のみ）
        if (request.ExpectedClassName != null)
        {
            var actualClass = GetClassName(hwnd);
            if (!actualClass.Equals(request.ExpectedClassName, StringComparison.Ordinal))
                return WindowGuardResult.Deny(
                    $"ウィンドウクラス名が期待値と一致しません（期待: {request.ExpectedClassName}, 実際: {actualClass}）");
        }

        // 4. マウス操作の場合、送信直前のクライアント領域内に座標が収まっているか
        if (request.ScreenPointForMouse is { } pt)
        {
            if (!User32Native.GetClientRect(hwnd, out var rect))
                return WindowGuardResult.Deny("対象ウィンドウのクライアント領域を取得できませんでした");

            var origin = new User32Native.POINT { X = 0, Y = 0 };
            if (!User32Native.ClientToScreen(hwnd, ref origin))
                return WindowGuardResult.Deny("対象ウィンドウのスクリーン座標への変換に失敗しました");

            var left = origin.X;
            var top = origin.Y;
            var right = origin.X + (rect.Right - rect.Left);
            var bottom = origin.Y + (rect.Bottom - rect.Top);

            if (pt.X < left || pt.X >= right || pt.Y < top || pt.Y >= bottom)
                return WindowGuardResult.Deny(
                    $"送信先座標({pt.X},{pt.Y})が対象ウィンドウのクライアント領域({left},{top})-({right},{bottom})の外にあります");
        }

        // 5. フォアグラウンド確認（送信直前に前面化し、実際に前面になったことを確認する）
        if (request.RequireForeground)
        {
            User32Native.SetForegroundWindow(hwnd);
            if (User32Native.GetForegroundWindow() != hwnd)
                return WindowGuardResult.Deny("対象ウィンドウを前面化できませんでした（別のウィンドウがフォアグラウンドのままです）");
        }

        return WindowGuardResult.Allow();
    }

    internal static string GetClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        User32Native.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    internal static string? TryGetExecutablePath(uint pid)
    {
        var hProcess = Kernel32Native.OpenProcess(Kernel32Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess == IntPtr.Zero) return null;

        try
        {
            var sb = new StringBuilder(1024);
            var size = (uint)sb.Capacity;
            if (!Kernel32Native.QueryFullProcessImageName(hProcess, 0, sb, ref size))
                return null;
            return sb.ToString();
        }
        finally
        {
            Kernel32Native.CloseHandle(hProcess);
        }
    }
}

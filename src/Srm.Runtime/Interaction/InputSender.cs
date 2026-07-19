using System.Runtime.InteropServices;
using Srm.Runtime.Native;

namespace Srm.Runtime.Interaction;

// WindowGuardの検証を通過した後にのみ呼ぶこと。このクラス自体はガードを一切
// 行わない（対象ウィンドウが正しいかの確認はWindowGuardの責務、こちらは送信のみ）。
// Tier1（ホスト、Srm.Mcp）・チャネルBのゲスト内実行（Srm.Cli --nested）の両方から
// 共通で使う。
public static class InputSender
{
    // Unicode文字として1文字ずつ送る（KEYEVENTF_UNICODE）。仮想キーコードへの
    // マッピングを介さないため、任意の言語のテキストをそのまま入力できる。
    // Enter/Tab等の特殊キーはこの経路では送れない（将来の拡張課題）。
    public static void SendUnicodeText(string text)
    {
        if (text.Length == 0) return;

        var inputs = new List<User32Native.INPUT>(text.Length * 2);
        foreach (var ch in text)
        {
            inputs.Add(KeyEvent(ch, keyUp: false));
            inputs.Add(KeyEvent(ch, keyUp: true));
        }

        Send(inputs.ToArray());
    }

    public static void SendLeftClick(int screenX, int screenY)
    {
        var (normX, normY) = NormalizeToVirtualDesktop(screenX, screenY);
        const uint absFlags = User32Native.MOUSEEVENTF_ABSOLUTE | User32Native.MOUSEEVENTF_VIRTUALDESK;

        Send(new[]
        {
            MouseEvent(normX, normY, User32Native.MOUSEEVENTF_MOVE | absFlags),
            MouseEvent(normX, normY, User32Native.MOUSEEVENTF_LEFTDOWN | absFlags),
            MouseEvent(normX, normY, User32Native.MOUSEEVENTF_LEFTUP | absFlags),
        });
    }

    private static (int X, int Y) NormalizeToVirtualDesktop(int screenX, int screenY)
    {
        var vx = User32Native.GetSystemMetrics(User32Native.SM_XVIRTUALSCREEN);
        var vy = User32Native.GetSystemMetrics(User32Native.SM_YVIRTUALSCREEN);
        var vw = User32Native.GetSystemMetrics(User32Native.SM_CXVIRTUALSCREEN);
        var vh = User32Native.GetSystemMetrics(User32Native.SM_CYVIRTUALSCREEN);
        if (vw <= 0) vw = 1;
        if (vh <= 0) vh = 1;

        var normX = (int)(((double)(screenX - vx) / vw) * 65535);
        var normY = (int)(((double)(screenY - vy) / vh) * 65535);
        return (normX, normY);
    }

    private static User32Native.INPUT KeyEvent(char ch, bool keyUp) => new()
    {
        type = User32Native.INPUT_KEYBOARD,
        U = new User32Native.InputUnion
        {
            ki = new User32Native.KEYBDINPUT
            {
                wVk = 0,
                wScan = ch,
                dwFlags = User32Native.KEYEVENTF_UNICODE | (keyUp ? User32Native.KEYEVENTF_KEYUP : 0),
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };

    private static User32Native.INPUT MouseEvent(int normX, int normY, uint flags) => new()
    {
        type = User32Native.INPUT_MOUSE,
        U = new User32Native.InputUnion
        {
            mi = new User32Native.MOUSEINPUT
            {
                dx = normX,
                dy = normY,
                mouseData = 0,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };

    private static void Send(User32Native.INPUT[] inputs)
    {
        var sent = User32Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<User32Native.INPUT>());
        if (sent != inputs.Length)
            throw new InvalidOperationException(
                $"SendInputが一部失敗しました（要求: {inputs.Length}, 成功: {sent}, GetLastError: {Marshal.GetLastWin32Error()}）");
    }
}

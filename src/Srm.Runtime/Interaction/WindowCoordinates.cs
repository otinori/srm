using Srm.Runtime.Native;

namespace Srm.Runtime.Interaction;

public static class WindowCoordinates
{
    public static (int X, int Y) ClientToScreen(IntPtr hwnd, int clientX, int clientY)
    {
        var pt = new User32Native.POINT { X = clientX, Y = clientY };
        User32Native.ClientToScreen(hwnd, ref pt);
        return (pt.X, pt.Y);
    }

    // WindowCapture.CapturePngはウィンドウ全体（GetWindowRect基準、タイトルバー・
    // メニューバー等の非クライアント領域を含む）をキャプチャするが、send_mouseの
    // 座標系はクライアント領域基準（GetClientRect+ClientToScreen）であり原点が
    // 一致しない（DC-017 review_trigger、実機で確認済み）。screenshotの画像上で
    // クライアント領域がどこから始まるかを呼び出し元に伝えるためのオフセット。
    public static (int X, int Y) GetClientOffsetWithinWindow(IntPtr hwnd)
    {
        if (!User32Native.GetWindowRect(hwnd, out var windowRect))
            throw new InvalidOperationException("対象ウィンドウの矩形を取得できませんでした");

        var clientOrigin = ClientToScreen(hwnd, 0, 0);
        return (clientOrigin.X - windowRect.Left, clientOrigin.Y - windowRect.Top);
    }
}

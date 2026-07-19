using System.Drawing;
using System.Drawing.Imaging;
using Srm.Runtime.Native;

namespace Srm.Runtime.Interaction;

// WindowGuardの検証を通過した後にのみ呼ぶこと（InputSenderと同じ理由）。
public static class WindowCapture
{
    public static byte[] CapturePng(IntPtr hwnd)
    {
        if (!User32Native.GetWindowRect(hwnd, out var rect))
            throw new InvalidOperationException("対象ウィンドウの矩形を取得できませんでした");

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException($"対象ウィンドウのサイズが不正です（{width}x{height}）");

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        var hdc = graphics.GetHdc();
        try
        {
            if (!User32Native.PrintWindow(hwnd, hdc, User32Native.PW_RENDERFULLCONTENT))
                throw new InvalidOperationException("PrintWindowに失敗しました");
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }

        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}

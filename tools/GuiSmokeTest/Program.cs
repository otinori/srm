using System.Runtime.InteropServices;

namespace Srm.Diag.GuiSmokeTest;

// AppContainer内で本当にウィンドウが表示され、人間がクリックできるかを確認するための
// 最小限のGUIテストアプリ。クリックのたびにカウンタを更新し、かつ同じディレクトリの
// ログファイルにも記録するため、見た目の描画だけでなく実際に入力が届いているかを
// ファイルからも後で確認できる。

internal static class Program
{
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation,
        uint tokenInformationLength, out uint returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private const int TokenIsAppContainer = 29;

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        var isAppContainer = QueryIsAppContainer();
        var pid = Environment.ProcessId;
        var logPath = Path.Combine(Environment.CurrentDirectory, "gui-smoke-test-clicks.log");

        var form = new Form
        {
            Text = $"SRM GUI Smoke Test — PID {pid} — IsAppContainer={isAppContainer}",
            Width = 480,
            Height = 220,
            StartPosition = FormStartPosition.CenterScreen,
            TopMost = true,
        };

        var label = new Label
        {
            Text = $"IsAppContainer = {isAppContainer}\nクリック回数: 0\nこのウィンドウが見えて、ボタンが押せれば成功です。",
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 100,
            TextAlign = ContentAlignment.MiddleCenter,
        };

        var count = 0;
        var button = new Button
        {
            Text = "ここをクリック",
            Dock = DockStyle.Bottom,
            Height = 60,
        };
        button.Click += (_, _) =>
        {
            count++;
            label.Text = $"IsAppContainer = {isAppContainer}\nクリック回数: {count}\nこのウィンドウが見えて、ボタンが押せれば成功です。";
            File.AppendAllText(logPath, $"{DateTime.Now:O} click #{count} pid={pid}{Environment.NewLine}");
        };

        form.Controls.Add(label);
        form.Controls.Add(button);

        File.AppendAllText(logPath, $"{DateTime.Now:O} window shown pid={pid} isAppContainer={isAppContainer}{Environment.NewLine}");

        Application.Run(form);
    }

    private static uint QueryIsAppContainer()
    {
        var proc = GetCurrentProcess();
        if (!OpenProcessToken(proc, 0x0008, out var token)) return 0xFFFFFFFF;
        try
        {
            var buf = Marshal.AllocHGlobal(4);
            try
            {
                if (GetTokenInformation(token, TokenIsAppContainer, buf, 4, out _))
                    return unchecked((uint)Marshal.ReadInt32(buf));
                return 0xFFFFFFFF;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }
}

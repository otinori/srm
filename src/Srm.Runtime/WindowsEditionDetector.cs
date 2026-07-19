using Microsoft.Win32;

namespace Srm.Runtime;

public static class WindowsEditionDetector
{
    public static bool IsHomeEdition()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var edition = key?.GetValue("EditionID") as string ?? "";
        return edition.Contains("Home", StringComparison.OrdinalIgnoreCase);
    }

    public static void ThrowIfTier2OnHome(int tier)
    {
        if (tier == 2 && IsHomeEdition())
            throw new NotSupportedException(
                "Windows Home エディションでは Tier 2 (Windows Sandbox) は使用できません。\n" +
                "次のいずれかを行ってください:\n" +
                "  1. ポリシーの tier を 1 (AppContainer) に変更する\n" +
                "  2. Windows Pro / Enterprise にアップグレードする");
    }
}

using Xunit;

namespace Srm.Runtime.Tests;

// 実プロセス起動・Job Object操作等の実Win32呼び出しを伴うテストは、Windows実機
// （CIのwindows-latestランナーを含む）でのみ意味がある。Linux開発環境では自動的に
// Skippedとして報告され、CIでは通常のFactとして実行される。
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows実機（またはCIのwindows-latestランナー）でのみ実行される";
    }
}

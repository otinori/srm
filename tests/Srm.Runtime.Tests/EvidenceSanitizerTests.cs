using Srm.Runtime.Evidence;
using Xunit;

namespace Srm.Runtime.Tests;

public class EvidenceSanitizerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "srm-test-" + Guid.NewGuid());

    public EvidenceSanitizerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void Scan_CleanDirectory_ReturnsNoViolations()
    {
        File.WriteAllText(Path.Combine(_dir, "report.txt"), "ok");
        var violations = EvidenceSanitizer.Scan(_dir);
        Assert.Empty(violations);
    }

    [Fact]
    public void Scan_ReservedDeviceName_ReturnsViolation()
    {
        // "CON.txt"のような予約デバイス名は、素のパスではWindowsがCONデバイスとして
        // 解釈してしまい実ファイルを作成できない（コンソールの有無で例外か無言の
        // 未作成かは環境依存）。`\\?\`（拡張長パス）プレフィックスでデバイス名解釈を
        // バイパスして確実に実ファイルとして作成する。EvidenceSanitizer.Scan側は
        // 通常パスのディレクトリ列挙のままで、そちらからも同じファイルとして見える。
        File.WriteAllText(@"\\?\" + Path.Combine(_dir, "CON.txt"), "x");
        var violations = EvidenceSanitizer.Scan(_dir);
        Assert.Contains(violations, v => v.RelativePath == "CON.txt" && v.Reason.Contains("予約デバイス名"));
    }

    [Fact]
    public void Scan_NonExistentDir_ReturnsEmpty()
    {
        var violations = EvidenceSanitizer.Scan(Path.Combine(_dir, "missing"));
        Assert.Empty(violations);
    }
}

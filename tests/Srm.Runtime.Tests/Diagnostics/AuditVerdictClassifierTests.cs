using System.Net;
using Srm.PolicyEngine.Models;
using Srm.Runtime.Diagnostics;
using Xunit;

namespace Srm.Runtime.Tests.Diagnostics;

// resource-access-audit-logging: AuditVerdictClassifierはネイティブAPI呼び出しを含まない
// 純粋なロジックのため、他のRuntimeテスト（WfpManagerTests等）と違いWindowsOnlyFactは
// 不要で、通常の[Fact]で実行できる。
public class AuditVerdictClassifierTests
{
    [Fact]
    public void ClassifyPath_PathInsideAllowedDirectory_ReturnsAllowed()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "srm-audit-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        try
        {
            var filesystem = new FilesystemPolicy { AllowPaths = new List<AllowedPath> { new() { Path = baseDir, Access = "r" } } };
            var sut = new AuditVerdictClassifier(filesystem, new NetworkPolicy());

            var verdict = sut.ClassifyPath(Path.Combine(baseDir, "sub", "file.txt"));

            Assert.Equal(AuditVerdict.Allowed, verdict);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public void ClassifyPath_PathOutsideAllowedDirectories_ReturnsWouldBlock()
    {
        var filesystem = new FilesystemPolicy
        {
            AllowPaths = new List<AllowedPath> { new() { Path = Path.Combine(Path.GetTempPath(), "srm-audit-allowed"), Access = "r" } },
        };
        var sut = new AuditVerdictClassifier(filesystem, new NetworkPolicy());

        var verdict = sut.ClassifyPath(Path.Combine(Path.GetTempPath(), "srm-audit-NOT-allowed", "file.txt"));

        Assert.Equal(AuditVerdict.WouldBlock, verdict);
    }

    [Fact]
    public void ClassifyPath_NoAllowPaths_AlwaysWouldBlock()
    {
        var sut = new AuditVerdictClassifier(new FilesystemPolicy(), new NetworkPolicy());

        var verdict = sut.ClassifyPath(Path.Combine(Path.GetTempPath(), "anything.txt"));

        Assert.Equal(AuditVerdict.WouldBlock, verdict);
    }

    [Fact]
    public void ClassifyHost_LoopbackWithNonEmptyAllowHosts_ReturnsAllowed()
    {
        // WfpManager.AddAllowRulesと同じ規約: allow_hostsが1件でもあれば
        // srm run実行時にループバックが自動的に許可される。
        var network = new NetworkPolicy { AllowHosts = new List<string> { "example.com" } };
        var sut = new AuditVerdictClassifier(new FilesystemPolicy(), network);

        Assert.Equal(AuditVerdict.Allowed, sut.ClassifyHost(IPAddress.Loopback));
        Assert.Equal(AuditVerdict.Allowed, sut.ClassifyHost(IPAddress.IPv6Loopback));
    }

    [Fact]
    public void ClassifyHost_LoopbackWithEmptyAllowHosts_ReturnsWouldBlock()
    {
        var sut = new AuditVerdictClassifier(new FilesystemPolicy(), new NetworkPolicy());

        Assert.Equal(AuditVerdict.WouldBlock, sut.ClassifyHost(IPAddress.Loopback));
    }

    [Fact]
    public void ClassifyHost_UnresolvedHost_ReturnsWouldBlock()
    {
        var network = new NetworkPolicy { AllowHosts = new List<string> { "example.com" } };
        var sut = new AuditVerdictClassifier(new FilesystemPolicy(), network);

        // example.comが解決するIPには一致しないはずの明らかに無関係なIPを使う。
        var verdict = sut.ClassifyHost(IPAddress.Parse("203.0.113.1"));

        Assert.Equal(AuditVerdict.WouldBlock, verdict);
    }
}

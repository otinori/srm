using Srm.Runtime.Sandbox;
using Xunit;

namespace Srm.Runtime.Tests;

public class OutboxQuotaMonitorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "srm-quota-" + Guid.NewGuid());

    public OutboxQuotaMonitorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void ComputeSize_NonExistentDir_ReturnsZero()
    {
        Assert.Equal(0, OutboxQuotaMonitor.ComputeSize(Path.Combine(_dir, "missing")));
    }

    [Fact]
    public void ComputeSize_SumsAllFilesRecursively()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "1234567890"); // 10 bytes
        var subDir = Path.Combine(_dir, "sub");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "b.txt"), "12345"); // 5 bytes

        Assert.Equal(15, OutboxQuotaMonitor.ComputeSize(_dir));
    }

    [Fact]
    public void IsOverQuota_SizeAtOrBelowLimit_ReturnsFalse()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "1234567890"); // 10 bytes
        Assert.False(OutboxQuotaMonitor.IsOverQuota(_dir, 10));
    }

    [Fact]
    public void IsOverQuota_SizeExceedsLimit_ReturnsTrue()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "1234567890"); // 10 bytes
        Assert.True(OutboxQuotaMonitor.IsOverQuota(_dir, 9));
    }
}

using Srm.PolicyIntegrity;
using Xunit;

namespace Srm.PolicyIntegrity.Tests;

public class IntegrityTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly IntegrityWriter _writer = new();
    private readonly IntegrityVerifier _verifier = new();

    public IntegrityTests() => Directory.CreateDirectory(_tmpDir);

    public void Dispose() => Directory.Delete(_tmpDir, true);

    private string CreatePolicy(string content = "name: test\ntier: 1\n")
    {
        var path = Path.Combine(_tmpDir, "policy.yaml");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Write_CreatesValidSidecar()
    {
        var policy = CreatePolicy();
        var sidecar = _writer.Write(policy);
        Assert.True(File.Exists(sidecar));
        Assert.Equal(64, File.ReadAllText(sidecar).Trim().Length);
    }

    [Fact]
    public void Verify_ValidSidecar_DoesNotThrow()
    {
        var policy = CreatePolicy();
        _writer.Write(policy);
        _verifier.Verify(policy);
    }

    [Fact]
    public void Verify_MissingSidecar_ThrowsIntegrityException()
    {
        var policy = CreatePolicy();
        var ex = Assert.Throws<IntegrityException>(() => _verifier.Verify(policy));
        Assert.Contains("サイドカー", ex.Message);
    }

    [Fact]
    public void Verify_TamperedPolicy_ThrowsIntegrityException()
    {
        var policy = CreatePolicy();
        _writer.Write(policy);
        File.AppendAllText(policy, "\n# tampered");
        var ex = Assert.Throws<IntegrityException>(() => _verifier.Verify(policy));
        Assert.Contains("改ざん", ex.Message);
    }

    [Fact]
    public void HashCalculator_SameContent_SameHash()
    {
        var p1 = Path.Combine(_tmpDir, "a.yaml");
        var p2 = Path.Combine(_tmpDir, "b.yaml");
        File.WriteAllText(p1, "same content");
        File.WriteAllText(p2, "same content");
        Assert.Equal(HashCalculator.ComputeFileSha256(p1), HashCalculator.ComputeFileSha256(p2));
    }
}

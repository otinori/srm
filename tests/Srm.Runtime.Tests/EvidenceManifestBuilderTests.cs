using Srm.Runtime.Evidence;
using Xunit;

namespace Srm.Runtime.Tests;

public class EvidenceManifestBuilderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "srm-test-" + Guid.NewGuid());
    private readonly EvidenceManifestBuilder _sut = new();

    public EvidenceManifestBuilderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void Build_ComputesShaAndSizeForEachFile()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "hello");
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(Path.Combine(_dir, "sub", "b.txt"), "world");

        var manifest = _sut.Build("app1", "run1", _dir);

        Assert.Equal(2, manifest.Files.Count);
        var a = manifest.Files.Single(f => f.RelativePath == "a.txt");
        Assert.Equal(5, a.SizeBytes);
        Assert.Equal(EvidenceManifestBuilder.ComputeSha256(Path.Combine(_dir, "a.txt")), a.Sha256);
        Assert.Contains(manifest.Files, f => f.RelativePath == "sub/b.txt");
    }

    [Fact]
    public void Build_NonExistentDir_ReturnsEmptyManifest()
    {
        var manifest = _sut.Build("app1", "run1", Path.Combine(_dir, "missing"));
        Assert.Empty(manifest.Files);
    }

    [Fact]
    public void WriteManifest_ThenRead_RoundTrips()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "hello");
        var manifest = _sut.Build("app1", "run1", _dir);
        var path = Path.Combine(_dir, "manifest.json");

        _sut.WriteManifest(manifest, path);
        var read = _sut.ReadManifest(path);

        Assert.NotNull(read);
        Assert.Equal(manifest.Files.Count, read!.Files.Count);
        Assert.Equal("app1", read.App);
    }
}

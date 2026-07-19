using Srm.Runtime.Operations;
using Xunit;

namespace Srm.Runtime.Tests.Operations;

public class ValidateOperationTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly ValidateOperation _sut = new();

    public ValidateOperationTests() => Directory.CreateDirectory(_tmpDir);

    public void Dispose() => Directory.Delete(_tmpDir, true);

    private const string ValidYaml = """
        name: test-app
        tier: 1
        application:
          executable: C:\Windows\System32\cmd.exe
        """;

    private string WritePolicy(string name, string yaml)
    {
        var path = Path.Combine(_tmpDir, $"{name}.yaml");
        File.WriteAllText(path, yaml);
        return path;
    }

    [Fact]
    public void Execute_Sign_WritesSidecarAndReturnsResult()
    {
        WritePolicy("test-app", ValidYaml);

        var result = _sut.Execute(_tmpDir, "test-app", sign: true);

        Assert.Equal("test-app", result.PolicyName);
        Assert.Equal(1, result.Tier);
        Assert.Equal(@"C:\Windows\System32\cmd.exe", result.Executable);
        Assert.NotNull(result.SidecarPath);
        Assert.True(File.Exists(result.SidecarPath));
    }

    [Fact]
    public void Execute_WithoutSidecar_ThrowsIntegrityError()
    {
        WritePolicy("test-app", ValidYaml);

        var ex = Assert.Throws<SrmOperationException>(() => _sut.Execute(_tmpDir, "test-app", sign: false));
        Assert.Equal(2, ex.ExitCode);
    }

    [Fact]
    public void Execute_InvalidPolicy_ThrowsValidationError()
    {
        WritePolicy("bad-app", "name: bad-app\ntier: 3\n");

        var ex = Assert.Throws<SrmOperationException>(() => _sut.Execute(_tmpDir, "bad-app", sign: true));
        Assert.Equal(1, ex.ExitCode);
    }

    [Fact]
    public void Execute_MissingFile_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() => _sut.Execute(_tmpDir, "nonexistent", sign: true));
    }
}

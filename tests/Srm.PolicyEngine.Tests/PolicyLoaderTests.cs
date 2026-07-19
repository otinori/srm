using Srm.PolicyEngine;
using Xunit;

namespace Srm.PolicyEngine.Tests;

public class PolicyLoaderTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly PolicyLoader _sut = new();

    public PolicyLoaderTests() => Directory.CreateDirectory(_tmpDir);

    public void Dispose() => Directory.Delete(_tmpDir, true);

    [Fact]
    public void Load_ValidYaml_ReturnsParsedPolicy()
    {
        var yaml = """
            name: test-app
            description: テスト
            tier: 1
            application:
              executable: C:\Windows\System32\cmd.exe
              arguments: /c echo hello
              working_directory: C:\tmp
            filesystem:
              allow_paths:
                - path: C:\tmp
                  access: rw
            network:
              allow_hosts:
                - example.com
            process:
              allow_child_processes: true
              max_processes: 5
            logging:
              level: info
              retention_days: 7
            """;

        var path = Path.Combine(_tmpDir, "test-app.yaml");
        File.WriteAllText(path, yaml);

        var policy = _sut.Load(path);

        Assert.Equal("test-app", policy.Name);
        Assert.Equal(1, policy.Tier);
        Assert.Equal(@"C:\Windows\System32\cmd.exe", policy.Application.Executable);
        Assert.Single(policy.Filesystem.AllowPaths);
        Assert.Single(policy.Network.AllowHosts);
    }

    [Fact]
    public void Load_MissingFile_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() =>
            _sut.Load(Path.Combine(_tmpDir, "nonexistent.yaml")));
    }

    [Fact]
    public void Load_ExpandsEnvVars()
    {
        Environment.SetEnvironmentVariable("SRM_TEST_EXE", @"C:\test\app.exe");
        var yaml = """
            name: env-test
            tier: 1
            application:
              executable: '%SRM_TEST_EXE%'
            """;
        var path = Path.Combine(_tmpDir, "env-test.yaml");
        File.WriteAllText(path, yaml);

        var policy = _sut.Load(path);
        Assert.Equal(@"C:\test\app.exe", policy.Application.Executable);
    }
}

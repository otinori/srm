using Srm.PolicyEngine;
using Srm.PolicyEngine.Models;
using Xunit;

namespace Srm.PolicyEngine.Tests;

public class PolicyWriterTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly PolicyWriter _sut = new();
    private readonly PolicyLoader _loader = new();

    public PolicyWriterTests() => Directory.CreateDirectory(_tmpDir);

    public void Dispose() => Directory.Delete(_tmpDir, true);

    private static PolicyModel BuildSamplePolicy() => new()
    {
        Name = "roundtrip-app",
        Description = "テスト",
        Tier = 2,
        Application = new ApplicationConfig
        {
            Executable = @"C:\apps\app.exe",
            Arguments = "--flag",
            WorkingDirectory = @"C:\apps",
        },
        Variables = new Dictionary<string, string> { ["HOME"] = @"C:\Users\test" },
        Filesystem = new FilesystemPolicy
        {
            AllowPaths = new List<AllowedPath>
            {
                new() { Path = @"C:\apps", Access = "rw" },
                new() { Path = @"C:\apps\logs", Access = "r" },
            },
        },
        Network = new NetworkPolicy { AllowHosts = new List<string> { "example.com", "api.example.com" } },
        Process = new ProcessPolicy { AllowChildProcesses = false, MaxProcesses = 3 },
        Logging = new LoggingPolicy { Level = "debug", RetentionDays = 14 },
    };

    [Fact]
    public void Serialize_UsesSnakeCaseKeys()
    {
        var yaml = _sut.Serialize(BuildSamplePolicy());

        Assert.Contains("working_directory:", yaml);
        Assert.Contains("allow_paths:", yaml);
        Assert.Contains("allow_hosts:", yaml);
        Assert.Contains("allow_child_processes:", yaml);
        Assert.Contains("max_processes:", yaml);
        Assert.Contains("retention_days:", yaml);
    }

    [Fact]
    public void Save_ThenLoadRaw_RoundTripsAllFields()
    {
        var original = BuildSamplePolicy();
        var path = Path.Combine(_tmpDir, "roundtrip-app.yaml");

        _sut.Save(original, path);
        var reloaded = _loader.LoadRaw(path);

        Assert.Equal(original.Name, reloaded.Name);
        Assert.Equal(original.Description, reloaded.Description);
        Assert.Equal(original.Tier, reloaded.Tier);
        Assert.Equal(original.Application.Executable, reloaded.Application.Executable);
        Assert.Equal(original.Application.Arguments, reloaded.Application.Arguments);
        Assert.Equal(original.Application.WorkingDirectory, reloaded.Application.WorkingDirectory);
        Assert.Equal(original.Variables, reloaded.Variables);
        Assert.Equal(original.Filesystem.AllowPaths.Count, reloaded.Filesystem.AllowPaths.Count);
        Assert.Equal(original.Filesystem.AllowPaths[0].Path, reloaded.Filesystem.AllowPaths[0].Path);
        Assert.Equal(original.Filesystem.AllowPaths[0].Access, reloaded.Filesystem.AllowPaths[0].Access);
        Assert.Equal(original.Network.AllowHosts, reloaded.Network.AllowHosts);
        Assert.Equal(original.Process.AllowChildProcesses, reloaded.Process.AllowChildProcesses);
        Assert.Equal(original.Process.MaxProcesses, reloaded.Process.MaxProcesses);
        Assert.Equal(original.Logging.Level, reloaded.Logging.Level);
        Assert.Equal(original.Logging.RetentionDays, reloaded.Logging.RetentionDays);
    }

    [Fact]
    public void Save_DoesNotExpandVars()
    {
        var policy = BuildSamplePolicy();
        policy.Application.Executable = "%HOME%\\app.exe";
        var path = Path.Combine(_tmpDir, "unexpanded.yaml");

        _sut.Save(policy, path);
        var reloaded = _loader.LoadRaw(path);

        Assert.Equal("%HOME%\\app.exe", reloaded.Application.Executable);
    }
}

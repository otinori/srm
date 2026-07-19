using Srm.PolicyEngine.Models;
using Xunit;

namespace Srm.PolicyEngine.Tests;

public class PolicyValidatorTests
{
    private readonly PolicyValidator _sut = new();

    private static PolicyModel ValidPolicy() => new()
    {
        Name = "test-app",
        Tier = 1,
        Application = new ApplicationConfig { Executable = @"C:\Windows\System32\cmd.exe" },
        Filesystem = new FilesystemPolicy
        {
            AllowPaths = new() { new AllowedPath { Path = @"C:\tmp", Access = "rw" } }
        },
        Process = new ProcessPolicy { AllowChildProcesses = true, MaxProcesses = 5 },
        Logging = new LoggingPolicy { Level = "info", RetentionDays = 7 },
    };

    [Fact]
    public void Validate_ValidPolicy_ReturnsNoErrors()
    {
        var result = _sut.Validate(ValidPolicy());
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_MissingName_ReturnsError()
    {
        var p = ValidPolicy(); p.Name = "";
        var result = _sut.Validate(p);
        Assert.Contains(result.Errors, e => e.Contains("name"));
    }

    [Fact]
    public void Validate_InvalidTier_ReturnsError()
    {
        var p = ValidPolicy(); p.Tier = 3;
        var result = _sut.Validate(p);
        Assert.Contains(result.Errors, e => e.Contains("tier"));
    }

    [Fact]
    public void Validate_MissingExecutable_ReturnsError()
    {
        var p = ValidPolicy(); p.Application.Executable = "";
        var result = _sut.Validate(p);
        Assert.Contains(result.Errors, e => e.Contains("executable"));
    }

    [Fact]
    public void Validate_InvalidAccessMode_ReturnsError()
    {
        var p = ValidPolicy();
        p.Filesystem.AllowPaths[0].Access = "rwx";
        var result = _sut.Validate(p);
        Assert.Contains(result.Errors, e => e.Contains("アクセスモード"));
    }

    [Fact]
    public void Validate_InvalidLogLevel_ReturnsError()
    {
        var p = ValidPolicy(); p.Logging.Level = "verbose";
        var result = _sut.Validate(p);
        Assert.Contains(result.Errors, e => e.Contains("logging.level"));
    }

    [Fact]
    public void Validate_ZeroMaxProcesses_ReturnsError()
    {
        var p = ValidPolicy(); p.Process.MaxProcesses = 0;
        var result = _sut.Validate(p);
        Assert.Contains(result.Errors, e => e.Contains("max_processes"));
    }

    [Fact]
    public void Validate_ProvisionOnTier1_ReturnsError()
    {
        var p = ValidPolicy();
        p.Tier = 1;
        p.Provision.Steps.Add("run: echo hi");
        var result = _sut.Validate(p);
        Assert.Contains(result.Errors, e => e.Contains("provision"));
    }

    [Fact]
    public void Validate_ProvisionOnTier2_ReturnsNoError()
    {
        var p = ValidPolicy();
        p.Tier = 2;
        p.Provision.Toolchain.Add(new ToolchainEntry { Name = "node", Version = "20.11.0" });
        p.Provision.Steps.Add("unzip: node");
        var result = _sut.Validate(p);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_ToolchainMissingName_ReturnsError()
    {
        var p = ValidPolicy();
        p.Tier = 2;
        p.Provision.Toolchain.Add(new ToolchainEntry { Name = "", Version = "1.0" });
        var result = _sut.Validate(p);
        Assert.Contains(result.Errors, e => e.Contains("toolchain"));
    }

    [Fact]
    public void Validate_ZeroEvidenceRetentionDays_ReturnsError()
    {
        var p = ValidPolicy(); p.Evidence.RetentionDays = 0;
        var result = _sut.Validate(p);
        Assert.Contains(result.Errors, e => e.Contains("evidence.retention_days"));
    }

    [Fact]
    public void Validate_ZeroMaxOutboxBytes_ReturnsError()
    {
        var p = ValidPolicy(); p.Evidence.MaxOutboxBytes = 0;
        var result = _sut.Validate(p);
        Assert.Contains(result.Errors, e => e.Contains("evidence.max_outbox_bytes"));
    }

    [Fact]
    public void Validate_UnsetMaxOutboxBytes_IsValid()
    {
        var p = ValidPolicy();
        var result = _sut.Validate(p);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_PositiveMaxOutboxBytes_IsValid()
    {
        var p = ValidPolicy(); p.Evidence.MaxOutboxBytes = 1024;
        var result = _sut.Validate(p);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_McpServerMissingName_ReturnsError()
    {
        var p = ValidPolicy();
        p.Mcp.AllowServers.Add(new McpServerEntry { Name = "", Command = @"C:\tools\mcp.exe" });
        var result = _sut.Validate(p);
        Assert.Contains(result.Errors, e => e.Contains("mcp.allow_servers") && e.Contains("name"));
    }

    [Fact]
    public void Validate_McpServerMissingCommand_ReturnsError()
    {
        var p = ValidPolicy();
        p.Mcp.AllowServers.Add(new McpServerEntry { Name = "github", Command = "" });
        var result = _sut.Validate(p);
        Assert.Contains(result.Errors, e => e.Contains("mcp.allow_servers") && e.Contains("command"));
    }

    [Fact]
    public void Validate_McpServerEmptyAllowToolsEntry_ReturnsError()
    {
        var p = ValidPolicy();
        p.Mcp.AllowServers.Add(new McpServerEntry
        {
            Name = "github",
            Command = @"C:\tools\mcp.exe",
            AllowTools = new List<string> { "list_issues", "" },
        });
        var result = _sut.Validate(p);
        Assert.Contains(result.Errors, e => e.Contains("mcp.allow_servers") && e.Contains("allow_tools"));
    }

    [Fact]
    public void Validate_McpServerValid_IsValid()
    {
        var p = ValidPolicy();
        p.Mcp.AllowServers.Add(new McpServerEntry
        {
            Name = "github",
            Command = @"C:\tools\mcp.exe",
            AllowTools = new List<string> { "list_issues" },
        });
        var result = _sut.Validate(p);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_McpServerNoAllowTools_IsValid()
    {
        var p = ValidPolicy();
        p.Mcp.AllowServers.Add(new McpServerEntry { Name = "github", Command = @"C:\tools\mcp.exe" });
        var result = _sut.Validate(p);
        Assert.True(result.IsValid);
    }
}

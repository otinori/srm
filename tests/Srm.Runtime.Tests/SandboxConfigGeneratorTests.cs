using Srm.Runtime.Sandbox;
using Xunit;

namespace Srm.Runtime.Tests;

public class SandboxConfigGeneratorTests
{
    private static SandboxLaunchSpec BasicSpec(bool networking = false) => new()
    {
        ToolingHostDir = @"C:\SRM\run\app1\run1\tooling",
        InputHostDir = @"C:\SRM\run\app1\run1\input",
        OutboxHostDir = @"C:\SRM\run\app1\run1\outbox",
        ControlHostDir = @"C:\SRM\run\app1\run1\control",
        NetworkingEnabled = networking,
        LogonCommand = @"C:\srm\tooling\srm.exe run app1 --nested --control-dir C:\srm\control",
    };

    [Fact]
    public void Generate_IncludesFourMappedFolders()
    {
        var doc = SandboxConfigGenerator.Generate(BasicSpec());
        var folders = doc.Root!.Element("MappedFolders")!.Elements("MappedFolder").ToList();
        Assert.Equal(4, folders.Count);
    }

    [Fact]
    public void Generate_OutboxAndControlAreReadWrite_InputAndToolingAreReadOnly()
    {
        var doc = SandboxConfigGenerator.Generate(BasicSpec());
        var folders = doc.Root!.Element("MappedFolders")!.Elements("MappedFolder").ToList();

        bool ReadOnlyOf(string hostFolder) => folders
            .Single(f => f.Element("HostFolder")!.Value == hostFolder)
            .Element("ReadOnly")!.Value == "true";

        Assert.True(ReadOnlyOf(@"C:\SRM\run\app1\run1\tooling"));
        Assert.True(ReadOnlyOf(@"C:\SRM\run\app1\run1\input"));
        Assert.False(ReadOnlyOf(@"C:\SRM\run\app1\run1\outbox"));
        Assert.False(ReadOnlyOf(@"C:\SRM\run\app1\run1\control"));
    }

    [Fact]
    public void Generate_ProvisionHostDirNull_HasFourMappedFolders()
    {
        var spec = BasicSpec() with { ProvisionHostDir = null };
        var doc = SandboxConfigGenerator.Generate(spec);
        var folders = doc.Root!.Element("MappedFolders")!.Elements("MappedFolder").ToList();
        Assert.Equal(4, folders.Count);
    }

    [Fact]
    public void Generate_ProvisionHostDirSet_AddsReadOnlyFifthMappedFolder()
    {
        var spec = BasicSpec() with { ProvisionHostDir = @"C:\SRM\toolchain-cache" };
        var doc = SandboxConfigGenerator.Generate(spec);
        var folders = doc.Root!.Element("MappedFolders")!.Elements("MappedFolder").ToList();

        Assert.Equal(5, folders.Count);
        var provisionFolder = folders.Single(f => f.Element("HostFolder")!.Value == @"C:\SRM\toolchain-cache");
        Assert.Equal(@"C:\srm\provision", provisionFolder.Element("SandboxFolder")!.Value);
        Assert.Equal("true", provisionFolder.Element("ReadOnly")!.Value);
    }

    [Fact]
    public void Generate_NetworkingDisabled_SetsDisableElement()
    {
        var doc = SandboxConfigGenerator.Generate(BasicSpec(networking: false));
        Assert.Equal("Disable", doc.Root!.Element("Networking")!.Value);
    }

    [Fact]
    public void Generate_NetworkingEnabled_SetsDefaultElement()
    {
        var doc = SandboxConfigGenerator.Generate(BasicSpec(networking: true));
        Assert.Equal("Default", doc.Root!.Element("Networking")!.Value);
    }

    [Fact]
    public void Generate_SetsLogonCommand()
    {
        var doc = SandboxConfigGenerator.Generate(BasicSpec());
        var command = doc.Root!.Element("LogonCommand")!.Element("Command")!.Value;
        Assert.Contains("--nested", command);
    }

    [Fact]
    public void WriteToFile_WritesValidXmlToDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), "srm-test-" + Guid.NewGuid() + ".wsb");
        try
        {
            SandboxConfigGenerator.WriteToFile(BasicSpec(), path);
            Assert.True(File.Exists(path));
            var reloaded = System.Xml.Linq.XDocument.Load(path);
            Assert.Equal("Configuration", reloaded.Root!.Name.LocalName);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}

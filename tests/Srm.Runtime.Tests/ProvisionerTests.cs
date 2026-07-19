using System.IO.Compression;
using Srm.PolicyEngine.Models;
using Srm.Runtime.Sandbox;
using Xunit;

namespace Srm.Runtime.Tests;

// "unzip:"ステップと未知ステップの扱いのみを対象にする。"run:"ステップは
// cmd.exeを起動するWindows専用の処理のため、AppContainerLauncher等と同様に
// 実機（Windows）でのみ検証可能（TESTING.md参照）。
public class ProvisionerTests : IDisposable
{
    private readonly string _sourceRoot = Path.Combine(Path.GetTempPath(), "srm-provision-src-" + Guid.NewGuid());
    private readonly string? _originalPath = Environment.GetEnvironmentVariable("PATH");
    private readonly Provisioner _sut = new();

    public ProvisionerTests() => Directory.CreateDirectory(_sourceRoot);

    public void Dispose()
    {
        if (Directory.Exists(_sourceRoot)) Directory.Delete(_sourceRoot, true);
        if (Directory.Exists(Provisioner.GuestWorkRoot)) Directory.Delete(Provisioner.GuestWorkRoot, true);
        Environment.SetEnvironmentVariable("PATH", _originalPath);
    }

    private void CreateFakeToolchainZip(string name, string entryFileName = "tool.exe")
    {
        var toolDir = Path.Combine(_sourceRoot, name);
        Directory.CreateDirectory(toolDir);
        var zipPath = Path.Combine(toolDir, $"{name}.zip");
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        zip.CreateEntry(entryFileName);
    }

    [Fact]
    public void Run_NoSteps_DoesNothing()
    {
        var provision = new ProvisionPolicy();
        var logs = new List<string>();

        _sut.Run(provision, _sourceRoot, null, logs.Add);

        Assert.Empty(logs);
    }

    [Fact]
    public void Run_UnzipStep_ExtractsIntoGuestWorkRoot()
    {
        CreateFakeToolchainZip("mytool");
        var provision = new ProvisionPolicy { Steps = { "unzip: mytool" } };
        var logs = new List<string>();

        _sut.Run(provision, _sourceRoot, null, logs.Add);

        Assert.True(File.Exists(Path.Combine(Provisioner.GuestWorkRoot, "mytool", "tool.exe")));
    }

    [Fact]
    public void Run_UnzipStep_AddsExtractedDirToPath()
    {
        CreateFakeToolchainZip("mytool");
        var provision = new ProvisionPolicy { Steps = { "unzip: mytool" } };

        _sut.Run(provision, _sourceRoot, null, _ => { });

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        Assert.Contains(Path.Combine(Provisioner.GuestWorkRoot, "mytool"), path);
    }

    [Fact]
    public void Run_UnzipStep_MissingSourceDir_Throws()
    {
        var provision = new ProvisionPolicy { Steps = { "unzip: does-not-exist" } };

        Assert.Throws<InvalidOperationException>(() => _sut.Run(provision, _sourceRoot, null, _ => { }));
    }

    [Fact]
    public void Run_UnzipStep_NoZipInSourceDir_Throws()
    {
        Directory.CreateDirectory(Path.Combine(_sourceRoot, "empty-tool"));
        var provision = new ProvisionPolicy { Steps = { "unzip: empty-tool" } };

        Assert.Throws<InvalidOperationException>(() => _sut.Run(provision, _sourceRoot, null, _ => { }));
    }

    [Fact]
    public void Run_UnknownStepFormat_LogsWarningAndDoesNotThrow()
    {
        var provision = new ProvisionPolicy { Steps = { "unsupported: whatever" } };
        var logs = new List<string>();

        _sut.Run(provision, _sourceRoot, null, logs.Add);

        Assert.Contains(logs, l => l.Contains("警告") && l.Contains("unsupported: whatever"));
    }
}

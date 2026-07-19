using Srm.PolicyEngine.Models;
using Srm.Runtime.Sandbox;
using Xunit;

namespace Srm.Runtime.Tests.Sandbox;

public class FileTransferRequestHandlerTests : IDisposable
{
    private readonly string _guestRoot = Path.Combine(Path.GetTempPath(), $"srm-filetransfer-test-{Guid.NewGuid():N}");
    private readonly FileTransferRequestHandler _sut = new();

    public void Dispose()
    {
        try { Directory.Delete(_guestRoot, recursive: true); } catch { /* ベストエフォート */ }
    }

    private PolicyModel PolicyWithAllowPath(string allowedDir) => new()
    {
        Name = "test-policy",
        Filesystem = new FilesystemPolicy
        {
            AllowPaths = new List<AllowedPath> { new() { Path = allowedDir, Access = "rw" } },
        },
    };

    [Fact]
    public void Handle_HostToGuest_CopiesStagedFileToDestPath()
    {
        var allowedDir = Path.Combine(_guestRoot, "allowed");
        Directory.CreateDirectory(allowedDir);
        var requestId = Guid.NewGuid().ToString();
        var stagingDir = Path.Combine(SandboxConfigGenerator.GuestInputDir(_guestRoot), "transfer", requestId);
        Directory.CreateDirectory(stagingDir);
        File.WriteAllText(Path.Combine(stagingDir, "payload.txt"), "hello");

        var request = new FileTransferRequestModel
        {
            RequestId = requestId,
            Direction = FileTransferDirection.HostToGuest,
            GuestPath = Path.Combine(allowedDir, "payload.txt"),
            FileName = "payload.txt",
        };

        var result = _sut.Handle(PolicyWithAllowPath(allowedDir), _guestRoot, request);

        Assert.True(result.Success);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(allowedDir, "payload.txt")));
    }

    [Fact]
    public void Handle_HostToGuest_DestOutsideAllowPaths_Fails()
    {
        var allowedDir = Path.Combine(_guestRoot, "allowed");
        Directory.CreateDirectory(allowedDir);
        var outsideDir = Path.Combine(_guestRoot, "outside");
        Directory.CreateDirectory(outsideDir);

        var requestId = Guid.NewGuid().ToString();
        var stagingDir = Path.Combine(SandboxConfigGenerator.GuestInputDir(_guestRoot), "transfer", requestId);
        Directory.CreateDirectory(stagingDir);
        File.WriteAllText(Path.Combine(stagingDir, "payload.txt"), "hello");

        var request = new FileTransferRequestModel
        {
            RequestId = requestId,
            Direction = FileTransferDirection.HostToGuest,
            GuestPath = Path.Combine(outsideDir, "payload.txt"),
            FileName = "payload.txt",
        };

        var result = _sut.Handle(PolicyWithAllowPath(allowedDir), _guestRoot, request);

        Assert.False(result.Success);
        Assert.False(File.Exists(Path.Combine(outsideDir, "payload.txt")));
    }

    [Fact]
    public void Handle_HostToGuest_MissingStagedFile_Fails()
    {
        var allowedDir = Path.Combine(_guestRoot, "allowed");
        Directory.CreateDirectory(allowedDir);

        var request = new FileTransferRequestModel
        {
            RequestId = Guid.NewGuid().ToString(),
            Direction = FileTransferDirection.HostToGuest,
            GuestPath = Path.Combine(allowedDir, "payload.txt"),
            FileName = "payload.txt",
        };

        var result = _sut.Handle(PolicyWithAllowPath(allowedDir), _guestRoot, request);

        Assert.False(result.Success);
    }

    [Fact]
    public void Handle_GuestToHost_CopiesSourceFileToOutboxTransferFolder()
    {
        var allowedDir = Path.Combine(_guestRoot, "allowed");
        Directory.CreateDirectory(allowedDir);
        File.WriteAllText(Path.Combine(allowedDir, "result.txt"), "output");

        var requestId = Guid.NewGuid().ToString();
        var request = new FileTransferRequestModel
        {
            RequestId = requestId,
            Direction = FileTransferDirection.GuestToHost,
            GuestPath = Path.Combine(allowedDir, "result.txt"),
            FileName = "result.txt",
        };

        var result = _sut.Handle(PolicyWithAllowPath(allowedDir), _guestRoot, request);

        Assert.True(result.Success);
        var expectedPath = Path.Combine(SandboxConfigGenerator.GuestOutboxDir(_guestRoot), "transfer", requestId, "result.txt");
        Assert.Equal("output", File.ReadAllText(expectedPath));
    }

    [Fact]
    public void Handle_GuestToHost_SourceOutsideAllowPaths_Fails()
    {
        var allowedDir = Path.Combine(_guestRoot, "allowed");
        Directory.CreateDirectory(allowedDir);
        var outsideDir = Path.Combine(_guestRoot, "outside");
        Directory.CreateDirectory(outsideDir);
        File.WriteAllText(Path.Combine(outsideDir, "secret.txt"), "should not leave");

        var request = new FileTransferRequestModel
        {
            RequestId = Guid.NewGuid().ToString(),
            Direction = FileTransferDirection.GuestToHost,
            GuestPath = Path.Combine(outsideDir, "secret.txt"),
            FileName = "secret.txt",
        };

        var result = _sut.Handle(PolicyWithAllowPath(allowedDir), _guestRoot, request);

        Assert.False(result.Success);
    }

    [Fact]
    public void Handle_FileNameWithDirectoryTraversal_Fails()
    {
        var allowedDir = Path.Combine(_guestRoot, "allowed");
        Directory.CreateDirectory(allowedDir);

        var request = new FileTransferRequestModel
        {
            RequestId = Guid.NewGuid().ToString(),
            Direction = FileTransferDirection.HostToGuest,
            GuestPath = Path.Combine(allowedDir, "payload.txt"),
            FileName = @"..\..\evil.txt",
        };

        var result = _sut.Handle(PolicyWithAllowPath(allowedDir), _guestRoot, request);

        Assert.False(result.Success);
    }
}

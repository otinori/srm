using Srm.Runtime.Sandbox;
using Xunit;

namespace Srm.Runtime.Tests;

public class SandboxRunPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "srm-run-" + Guid.NewGuid());

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void Constructor_ComputesInputOutboxControlUnderAppAndRunId()
    {
        var paths = new SandboxRunPaths("my-app", "run-001", _root);

        Assert.Equal(Path.Combine(_root, "my-app", "run-001"), paths.RunRoot);
        Assert.Equal(Path.Combine(_root, "my-app", "run-001", "input"), paths.Input);
        Assert.Equal(Path.Combine(_root, "my-app", "run-001", "outbox"), paths.Outbox);
        Assert.Equal(Path.Combine(_root, "my-app", "run-001", "control"), paths.Control);
    }

    [Fact]
    public void EnsureCreated_CreatesAllThreeDirectories()
    {
        var paths = new SandboxRunPaths("my-app", "run-001", _root);
        paths.EnsureCreated();

        Assert.True(Directory.Exists(paths.Input));
        Assert.True(Directory.Exists(paths.Outbox));
        Assert.True(Directory.Exists(paths.Control));
    }
}

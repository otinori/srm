using ModelContextProtocol;
using Srm.Mcp.Tools;
using Xunit;

namespace Srm.Mcp.Tests;

public class RuntimeToolsTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly RuntimeTools _sut;

    public RuntimeToolsTests()
    {
        Directory.CreateDirectory(_tmpDir);
        _sut = new RuntimeTools(new PoliciesDir(_tmpDir));
    }

    public void Dispose() => Directory.Delete(_tmpDir, true);

    private const string ValidYaml = """
        name: test-app
        tier: 1
        application:
          executable: C:\Windows\System32\cmd.exe
        """;

    [Fact]
    public void Validate_ValidPolicy_ReturnsResult()
    {
        File.WriteAllText(Path.Combine(_tmpDir, "test-app.yaml"), ValidYaml);

        var result = _sut.Validate("test-app", sign: true);

        Assert.Equal("test-app", result.PolicyName);
        Assert.Equal(1, result.Tier);
        Assert.NotNull(result.SidecarPath);
    }

    [Fact]
    public void Validate_InvalidPolicy_ThrowsMcpExceptionWithDetail()
    {
        File.WriteAllText(Path.Combine(_tmpDir, "bad-app.yaml"), "name: bad-app\ntier: 9\n");

        var ex = Assert.Throws<McpException>(() => _sut.Validate("bad-app", sign: true));
        Assert.Contains("バリデーションに失敗", ex.Message);
    }

    [Fact]
    public void Validate_MissingSidecar_ThrowsMcpExceptionWithDetail()
    {
        File.WriteAllText(Path.Combine(_tmpDir, "test-app.yaml"), ValidYaml);

        var ex = Assert.Throws<McpException>(() => _sut.Validate("test-app", sign: false));
        Assert.Contains("整合性エラー", ex.Message);
    }

    [Fact]
    public void List_NoRunningApps_ReturnsEmpty()
    {
        // RunningAppRegistryは%ProgramData%\SRM\run\running.jsonというマシン全体の
        // 状態を見るため、他の実行結果が混入しないことまでは保証できない。ここでは
        // 例外を投げずに呼び出せることだけを確認する（実際の起動確認はTier1/Tier2の
        // 実機テストで行う）。
        var result = _sut.List();
        Assert.NotNull(result);
    }

    [Fact]
    public void Logs_NoLogs_ReturnsEmpty()
    {
        var result = _sut.Logs($"nonexistent-app-{Guid.NewGuid()}");
        Assert.Empty(result);
    }
}

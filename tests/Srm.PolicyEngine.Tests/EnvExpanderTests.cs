using Srm.PolicyEngine;
using Xunit;

namespace Srm.PolicyEngine.Tests;

public class EnvExpanderTests
{
    private readonly EnvExpander _sut = new();

    [Fact]
    public void Expand_SystemVar_ReturnsExpanded()
    {
        Environment.SetEnvironmentVariable("SRM_TEST_VAR", "hello");
        var result = _sut.Expand("%SRM_TEST_VAR%\\world");
        Assert.Equal("hello\\world", result);
    }

    [Fact]
    public void Expand_UserVar_OverridesSystemVar()
    {
        Environment.SetEnvironmentVariable("SRM_TEST_VAR", "system");
        var vars = new Dictionary<string, string> { ["SRM_TEST_VAR"] = "user" };
        var result = _sut.Expand("%SRM_TEST_VAR%", vars);
        Assert.Equal("user", result);
    }

    [Fact]
    public void Expand_UnknownVar_LeavesAsIs()
    {
        var result = _sut.Expand("%UNKNOWN_XYZ_123%");
        Assert.Equal("%UNKNOWN_XYZ_123%", result);
    }

    [Fact]
    public void Expand_EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", _sut.Expand(""));
    }

    [Fact]
    public void Expand_MultipleVars_ExpandsAll()
    {
        Environment.SetEnvironmentVariable("SRM_A", "foo");
        Environment.SetEnvironmentVariable("SRM_B", "bar");
        var result = _sut.Expand("%SRM_A%\\%SRM_B%");
        Assert.Equal("foo\\bar", result);
    }
}

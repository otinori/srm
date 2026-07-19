using Xunit;

namespace Srm.Runtime.Tests;

public class PathAllowlistTests
{
    [Fact]
    public void IsWithinAllowedPaths_ExactMatch_ReturnsTrue()
    {
        Assert.True(PathAllowlist.IsWithinAllowedPaths(@"C:\srm\outbox", new[] { @"C:\srm\outbox" }));
    }

    [Fact]
    public void IsWithinAllowedPaths_Descendant_ReturnsTrue()
    {
        Assert.True(PathAllowlist.IsWithinAllowedPaths(@"C:\srm\outbox\sub\file.txt", new[] { @"C:\srm\outbox" }));
    }

    [Fact]
    public void IsWithinAllowedPaths_SiblingWithMatchingPrefix_ReturnsFalse()
    {
        // "C:\srm\out"は"C:\srm\outbox"の文字列プレフィックスだが、ディレクトリ境界を
        // 跨いだ誤マッチであってはならない。
        Assert.False(PathAllowlist.IsWithinAllowedPaths(@"C:\srm\outboxevil\file.txt", new[] { @"C:\srm\outbox" }));
    }

    [Fact]
    public void IsWithinAllowedPaths_OutsideAllAllowedPaths_ReturnsFalse()
    {
        Assert.False(PathAllowlist.IsWithinAllowedPaths(@"C:\Windows\System32\evil.exe", new[] { @"C:\srm\outbox" }));
    }

    [Fact]
    public void IsWithinAllowedPaths_PathTraversalEscapingAllowedPath_ReturnsFalse()
    {
        Assert.False(PathAllowlist.IsWithinAllowedPaths(@"C:\srm\outbox\..\..\Windows\evil.exe", new[] { @"C:\srm\outbox" }));
    }

    [Theory]
    [InlineData("file.txt", true)]
    [InlineData("", false)]
    [InlineData(@"..\evil.txt", false)]
    [InlineData(@"sub\file.txt", false)]
    [InlineData(@"C:\srm\file.txt", false)]
    public void IsBareFileName_ValidatesNoDirectoryComponent(string fileName, bool expected)
    {
        Assert.Equal(expected, PathAllowlist.IsBareFileName(fileName));
    }
}

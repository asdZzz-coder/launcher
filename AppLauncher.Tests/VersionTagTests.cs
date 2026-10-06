using AppLauncher.Services;

namespace AppLauncher.Tests;

public class VersionTagTests
{
    [Theory]
    [InlineData("v1.0.15", "v1.0.9", 1)]
    [InlineData("v1.1.0", "1.0.99", 1)]
    [InlineData("v1.0.15", "1.0.15.0", 0)]
    [InlineData("v2", "v1.9", 1)]
    [InlineData("v2.0.0-beta", "v1.9.9", 1)]
    [InlineData("v1.0.0", "v1.0.1", -1)]
    public void Compare_UsesNumericVersion(string a, string b, int expectedSign) =>
        Assert.Equal(expectedSign, Math.Sign(VersionTag.Compare(a, b)));

    [Fact]
    public void Parse_UnknownTag_ReturnsNull() => Assert.Null(VersionTag.Parse("latest"));

    [Fact]
    public void IsNewer_FalseForSameVersion() => Assert.False(VersionTag.IsNewer("v1.0.0", "1.0.0"));
}

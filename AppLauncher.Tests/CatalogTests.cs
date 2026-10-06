using AppLauncher.Services;

namespace AppLauncher.Tests;

public class CatalogTests
{
    [Fact]
    public void BuiltInCatalog_IsValid()
    {
        var apps = Catalog.Parse(File.ReadAllText(Path.Combine(FindRepoRoot(), "AppLauncher", "apps.json")));
        Assert.NotEmpty(apps);
        Assert.All(apps, a => Assert.EndsWith(".exe", a.Exe));
    }

    [Theory]
    [InlineData("""[{ "id": "a b", "name": "x", "repo": "o/r", "asset": "x", "exe": "x.exe" }]""")]
    [InlineData("""[{ "id": "a", "name": "x", "repo": "noslash", "asset": "x", "exe": "x.exe" }]""")]
    [InlineData("""[{ "id": "a", "name": "x", "repo": "o/r", "asset": "(", "exe": "x.exe" }]""")]
    [InlineData("""[{ "id": "a", "name": "x", "repo": "o/r", "asset": "x", "exe": "x.exe" }, { "id": "A", "name": "y", "repo": "o/r", "asset": "x", "exe": "x.exe" }]""")]
    public void Parse_RejectsInvalidEntries(string json) =>
        Assert.Throws<FormatException>(() => Catalog.Parse(json));

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "AppLauncher.slnx"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }
}

using AppLauncher.Models;
using AppLauncher.Services;

namespace AppLauncher.Tests;

public sealed class LauncherSettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"AppLauncherTests-{Guid.NewGuid():N}", "settings.json");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_path)!, recursive: true); } catch (IOException) { }
    }

    private static AppDefinition App(string id) => new(id, id, "o/r", "x", "x.exe");

    [Fact]
    public void ApplyOrder_UsesSavedOrder_NewAppsLast()
    {
        var apps = new[] { App("a"), App("b"), App("c"), App("new1"), App("new2") };

        var ordered = LauncherSettings.ApplyOrder(apps, ["c", "a", "removed", "b"]);

        Assert.Equal(["c", "a", "b", "new1", "new2"], ordered.Select(a => a.Id));
    }

    [Fact]
    public void ApplyOrder_EmptyOrder_KeepsCatalogOrder()
    {
        var apps = new[] { App("a"), App("b") };
        Assert.Equal(["a", "b"], LauncherSettings.ApplyOrder(apps, []).Select(a => a.Id));
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        new LauncherSettings { Order = ["b", "a"], ViewMode = LauncherSettings.ListView }.Save(_path);

        var loaded = LauncherSettings.Load(_path);

        Assert.Equal(["b", "a"], loaded.Order);
        Assert.Equal(LauncherSettings.ListView, loaded.ViewMode);
    }

    [Fact]
    public void Load_MissingOrBrokenFile_ReturnsDefaults()
    {
        Assert.Equal(LauncherSettings.GridView, LauncherSettings.Load(_path).ViewMode);

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{ not json");
        var loaded = LauncherSettings.Load(_path);
        Assert.Equal(LauncherSettings.GridView, loaded.ViewMode);
        Assert.Empty(loaded.Order);
    }

    [Fact]
    public void Load_UnknownViewMode_FallsBackToGrid()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, """{ "ViewMode": "Weird", "Order": null }""");

        var loaded = LauncherSettings.Load(_path);

        Assert.Equal(LauncherSettings.GridView, loaded.ViewMode);
        Assert.Empty(loaded.Order);
    }
}

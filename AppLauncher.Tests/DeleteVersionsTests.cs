using System.IO.Compression;
using AppLauncher.Models;
using AppLauncher.Services;
using AppLauncher.ViewModels;

namespace AppLauncher.Tests;

/// <summary>勾選要刪除的版本。</summary>
public sealed class DeleteVersionsTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "AppLauncherTests-" + Guid.NewGuid().ToString("N"));
    private readonly AppStore _store;
    private readonly GitHubService _github;
    private readonly AppDefinition _app = new("a", "測試", "o/r", "\\.zip$", "App.exe");

    public DeleteVersionsTests()
    {
        Directory.CreateDirectory(_temp);
        _store = new AppStore(Path.Combine(_temp, "apps"));
        _github = new GitHubService(Path.Combine(_temp, "cache.json"));
    }

    public void Dispose()
    {
        _github.Dispose();
        try { Directory.Delete(_temp, recursive: true); } catch (IOException) { }
    }

    private AppItemViewModel Install(params string[] tags)
    {
        var zip = Path.Combine(_temp, "a.zip");
        if (!File.Exists(zip))
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("App.exe").Open()))
                writer.Write("MZ");
        foreach (var tag in tags) _store.Install(_app, tag, "a.zip", zip);
        var item = new AppItemViewModel(_app, _store, _github);
        item.ReloadInstalled();
        return item;
    }

    [Fact]
    public void KeepLatest_SelectsAllButNewest()
    {
        var versions = Install("v1.0.0", "v1.1.0", "v1.2.0").Versions.Select(o => o.Version).ToList();
        var vm = new DeleteVersionsViewModel("測試", versions, _ => false);

        Assert.False(vm.HasSelection);
        vm.KeepLatestCommand.Execute(null);

        Assert.Equal(["v1.1.0", "v1.0.0"], vm.Selected.Select(v => v.Tag));
        Assert.True(vm.Choices[0].IsLatest);
        Assert.StartsWith("刪除 2 個版本", vm.DeleteText);
    }

    [Fact]
    public void RunningVersion_CannotBeChecked()
    {
        var versions = Install("v1.0.0", "v1.1.0").Versions.Select(o => o.Version).ToList();
        var vm = new DeleteVersionsViewModel("測試", versions, v => v.Tag == "v1.0.0");

        var running = vm.Choices.Single(c => c.Tag == "v1.0.0");
        running.IsChecked = true;

        Assert.False(running.CanDelete);
        Assert.False(running.IsChecked);
        Assert.False(vm.HasSelection);
    }

    [Fact]
    public void DeleteVersions_DeletesOnlyChosen()
    {
        var item = Install("v1.0.0", "v1.1.0", "v1.2.0");
        var chosen = item.Versions.Select(o => o.Version).Where(v => v.Tag != "v1.1.0").ToList();

        item.DeleteVersions(chosen);

        Assert.Equal(["v1.1.0"], item.Versions.Select(o => o.Version.Tag));
        Assert.Equal("已刪除 v1.2.0、v1.0.0", item.Status);
        Assert.False(item.HasError);
    }

    [Fact]
    public void DeleteVersions_AllChosen_RemovesAppFolder()
    {
        var item = Install("v1.0.0", "v1.1.0");

        item.DeleteVersions(item.Versions.Select(o => o.Version).ToList());

        Assert.False(item.IsInstalled);
        Assert.False(Directory.Exists(_store.AppDirectory(_app)));
    }
}

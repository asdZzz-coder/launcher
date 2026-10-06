using System.IO.Compression;
using AppLauncher.Models;
using AppLauncher.Services;
using AppLauncher.ViewModels;

namespace AppLauncher.Tests;

/// <summary>線上 APP 清單套用到畫面上的清單：新增、移除、修改、標示「新」。</summary>
public sealed class CatalogSyncTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "AppLauncherTests-" + Guid.NewGuid().ToString("N"));
    private readonly AppStore _store;
    private readonly GitHubService _github;
    private string SettingsPath => Path.Combine(_temp, "settings.json");

    public CatalogSyncTests()
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

    private static AppDefinition App(string id, string? name = null) => new(id, name ?? id, "o/r", "\\.zip$", "App.exe");

    private MainViewModel CreateVm(params AppDefinition[] apps) =>
        new(apps, _store, _github, LauncherSettings.Load(SettingsPath), SettingsPath);

    private void Install(AppDefinition app)
    {
        var zip = Path.Combine(_temp, app.Id + ".zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("App.exe").Open()))
            writer.Write("MZ");
        _store.Install(app, "v1.0.0", "a.zip", zip);
    }

    [Fact]
    public void FirstRun_ExistingAppsAreNotNew()
    {
        var vm = CreateVm(App("a"), App("b"));

        Assert.All(vm.Apps, a => Assert.False(a.IsNew));
        Assert.Equal(0, vm.NewCount);
    }

    [Fact]
    public void NewAppFromGitHub_AddedAtEndAndMarkedNew()
    {
        var vm = CreateVm(App("a"), App("b"));

        vm.ApplyCatalog([App("a"), App("c"), App("b")]);

        Assert.Equal(["a", "b", "c"], vm.Apps.Select(a => a.Definition.Id));
        Assert.True(vm.Apps.Single(a => a.Definition.Id == "c").IsNew);
        Assert.Equal(1, vm.NewCount);
        Assert.Contains("c", vm.NewAppsText);
    }

    [Fact]
    public void NewFlag_SurvivesRestart_UntilDismissed()
    {
        CreateVm(App("a")).ApplyCatalog([App("a"), App("c")]);

        var restarted = CreateVm(App("a"), App("c"));
        restarted.ApplyCatalog([App("a"), App("c")]);
        Assert.True(restarted.Apps.Single(a => a.Definition.Id == "c").IsNew);

        restarted.DismissNewAppsCommand.Execute(null);
        Assert.Equal(0, restarted.NewCount);
        Assert.Equal(0, CreateVm(App("a"), App("c")).NewCount);
    }

    [Fact]
    public void RemovedApp_RemovedUnlessInstalled()
    {
        var kept = App("kept");
        Install(kept);
        var vm = CreateVm(App("a"), App("gone"), kept);
        foreach (var app in vm.Apps) app.ReloadInstalled();

        vm.ApplyCatalog([App("a")]);

        Assert.Equal(["a", "kept"], vm.Apps.Select(a => a.Definition.Id));
    }

    [Fact]
    public void ChangedDefinition_UpdatesCard()
    {
        var vm = CreateVm(App("a", "舊名稱"));

        vm.ApplyCatalog([App("a", "新名稱")]);

        Assert.Equal("新名稱", Assert.Single(vm.Apps).Name);
    }

    [Fact]
    public void InstalledNewApp_IsNotNew()
    {
        var vm = CreateVm(App("a"));
        var c = App("c");
        Install(c);

        vm.ApplyCatalog([App("a"), c]);

        Assert.False(vm.Apps.Single(a => a.Definition.Id == "c").IsNew);
    }
}

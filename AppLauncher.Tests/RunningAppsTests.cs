using System.Diagnostics;
using System.IO.Compression;
using AppLauncher.Models;
using AppLauncher.Services;
using AppLauncher.ViewModels;

namespace AppLauncher.Tests;

/// <summary>偵測開著的 APP：「開啟」換成「關閉」，關掉後不再顯示「已開啟」。</summary>
public sealed class RunningAppsTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "AppLauncherTests-" + Guid.NewGuid().ToString("N"));
    private readonly AppStore _store;
    private readonly GitHubService _github;

    public RunningAppsTests()
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

    private AppItemViewModel InstallApp(params string[] tags)
    {
        var app = new AppDefinition("a", "測試", "o/r", "\\.zip$", "App.exe");
        var zip = Path.Combine(_temp, "a.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("App.exe").Open()))
            writer.Write("MZ");
        foreach (var tag in tags) _store.Install(app, tag, "a.zip", zip);
        var item = new AppItemViewModel(app, _store, _github);
        item.ReloadInstalled();
        return item;
    }

    private static HashSet<string> Running(params InstalledVersion[] versions) =>
        new(versions.Select(v => Path.GetFullPath(v.ExePath)), StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void GetRunningExePaths_FindsCurrentProcess()
    {
        var self = Environment.ProcessPath!;

        var running = RunningApps.GetRunningExePaths([self]);

        Assert.Contains(self, running);
    }

    [Fact]
    public void Running_ShowsCloseAndStatus_ThenClearsAfterExit()
    {
        var item = InstallApp("v1.0.0");
        var version = item.SelectedVersion!.Version;

        item.UpdateRunning(Running(version), DateTime.UtcNow);
        Assert.True(item.IsRunning);
        Assert.True(item.CloseCommand.CanExecute(null));
        Assert.False(item.LaunchCommand.CanExecute(null));
        Assert.Equal("已開啟 v1.0.0", item.Status);

        item.UpdateRunning(Running(), DateTime.UtcNow);
        Assert.False(item.IsRunning);
        Assert.True(item.LaunchCommand.CanExecute(null));
        Assert.Null(item.Status);
        Assert.Equal("版本 v1.0.0", item.DisplayStatus); // 不再顯示「已開啟」，但版本照樣顯示
    }

    [Fact]
    public void OtherVersionRunning_SelectedVersionCanStillOpen()
    {
        var item = InstallApp("v1.0.0", "v1.1.0");
        var old = item.Versions.Single(o => o.Version.Tag == "v1.0.0").Version;

        item.UpdateRunning(Running(old), DateTime.UtcNow);

        Assert.Equal("v1.1.0", item.SelectedVersion!.Version.Tag);
        Assert.False(item.IsRunning);
        Assert.Equal("已開啟 v1.0.0", item.Status);

        item.SelectedVersion = item.Versions.Single(o => o.Version.Tag == "v1.0.0");
        Assert.True(item.IsRunning);
    }

    [Fact]
    public async Task CloseAsync_NoWindow_TimesOut_ThenKillWorks()
    {
        using var process = Process.Start(new ProcessStartInfo("ping", "-n 30 127.0.0.1")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;

        Assert.False(await RunningApps.CloseAsync([process], TimeSpan.FromMilliseconds(300)));
        Assert.True(await RunningApps.KillAsync([process], TimeSpan.FromSeconds(5)));
        Assert.True(process.HasExited);
    }
}

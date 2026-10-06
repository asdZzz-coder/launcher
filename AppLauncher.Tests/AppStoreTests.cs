using System.IO.Compression;
using AppLauncher.Models;
using AppLauncher.Services;

namespace AppLauncher.Tests;

public sealed class AppStoreTests : IDisposable
{
    private static readonly AppDefinition CarLog = new("carlog", "車輛紀錄", "asdZzz-coder/car", "\\.zip$", "CarLog.exe");

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "AppLauncherTests-" + Guid.NewGuid().ToString("N"));
    private readonly AppStore _store;

    public AppStoreTests()
    {
        Directory.CreateDirectory(_temp);
        _store = new AppStore(Path.Combine(_temp, "apps"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch (IOException) { }
    }

    /// <summary>做一個跟 ClickOnce 安裝包同樣結構的 zip。</summary>
    private string MakeZip(string name, params string[] entries)
    {
        var path = Path.Combine(_temp, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
            writer.Write(entry);
        }
        return path;
    }

    [Fact]
    public void Install_FindsExeInsideClickOnceFolder()
    {
        var zip = MakeZip("a.zip", "CarLog.application", "Application Files/CarLog_1_1_0_0/CarLog.exe",
            "Application Files/CarLog_1_1_0_0/CarLog.dll");

        var installed = _store.Install(CarLog, "v1.1.0", "CarLog-ClickOnce.zip", zip);

        Assert.True(File.Exists(installed.ExePath));
        Assert.EndsWith(Path.Combine("CarLog_1_1_0_0", "CarLog.exe"), installed.ExePath);
        Assert.Equal("v1.1.0", Assert.Single(_store.GetInstalled(CarLog)).Tag);
    }

    [Fact]
    public void Install_RenamesDeployFiles()
    {
        var zip = MakeZip("a.zip", "Application Files/CarLog_1_0_0_0/CarLog.exe.deploy",
            "Application Files/CarLog_1_0_0_0/Assets/app.ico.deploy");

        var installed = _store.Install(CarLog, "v1.0.0", "CarLog.zip", zip);

        Assert.True(File.Exists(installed.ExePath));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(installed.ExePath)!, "Assets", "app.ico")));
    }

    [Fact]
    public void Install_SingleExeAsset()
    {
        var exe = Path.Combine(_temp, "download.tmp");
        File.WriteAllText(exe, "MZ");
        var ledger = CarLog with { Id = "ledger", Exe = "Ledger.exe" };

        var installed = _store.Install(ledger, "v1.0.14", "Ledger.exe", exe);

        Assert.Equal("Ledger.exe", Path.GetFileName(installed.ExePath));
    }

    [Fact]
    public void Install_MissingExe_ThrowsAndLeavesNothing()
    {
        var zip = MakeZip("a.zip", "readme.txt");

        Assert.Throws<InvalidOperationException>(() => _store.Install(CarLog, "v1.0.0", "a.zip", zip));
        Assert.Empty(_store.GetInstalled(CarLog));
        Assert.Empty(Directory.GetDirectories(_store.AppDirectory(CarLog)));
    }

    [Fact]
    public void Update_KeepsOldVersions_NewestFirst()
    {
        _store.Install(CarLog, "v1.0.9", "a.zip", MakeZip("a.zip", "CarLog.exe"));
        _store.Install(CarLog, "v1.0.10", "b.zip", MakeZip("b.zip", "CarLog.exe"));
        _store.Install(CarLog, "v1.1.0", "c.zip", MakeZip("c.zip", "CarLog.exe"));

        Assert.Equal(["v1.1.0", "v1.0.10", "v1.0.9"], _store.GetInstalled(CarLog).Select(v => v.Tag));
    }

    [Fact]
    public void Delete_RemovesOnlyThatVersion()
    {
        var old = _store.Install(CarLog, "v1.0.0", "a.zip", MakeZip("a.zip", "CarLog.exe"));
        _store.Install(CarLog, "v1.1.0", "b.zip", MakeZip("b.zip", "CarLog.exe"));

        _store.Delete(old);

        Assert.Equal("v1.1.0", Assert.Single(_store.GetInstalled(CarLog)).Tag);
        Assert.False(Directory.Exists(old.Directory));
    }

    [Fact]
    public void Delete_FileInUse_ThrowsAndKeepsVersionIntact()
    {
        var v = _store.Install(CarLog, "v1.0.0", "a.zip", MakeZip("a.zip", "CarLog.exe"));

        using (File.Open(v.ExePath, FileMode.Open, FileAccess.Read, FileShare.Read)) // 模擬程式還開著
            Assert.Throws<IOException>(() => _store.Delete(v));

        Assert.Single(_store.GetInstalled(CarLog));
    }

    [Fact]
    public void DeleteAll_RemovesAppFolder()
    {
        _store.Install(CarLog, "v1.0.0", "a.zip", MakeZip("a.zip", "CarLog.exe"));
        _store.Install(CarLog, "v1.1.0", "b.zip", MakeZip("b.zip", "CarLog.exe"));

        _store.DeleteAll(CarLog);

        Assert.Empty(_store.GetInstalled(CarLog));
        Assert.False(Directory.Exists(_store.AppDirectory(CarLog)));
    }

    [Fact]
    public void GetInstalled_IgnoresUnfinishedFolders()
    {
        var unfinished = Path.Combine(_store.AppDirectory(CarLog), "v9.9.9");
        Directory.CreateDirectory(unfinished);
        File.WriteAllText(Path.Combine(unfinished, "CarLog.exe"), "MZ"); // 沒有標記檔＝沒裝完

        Assert.Empty(_store.GetInstalled(CarLog));
    }

    [Fact]
    public void CleanupLeftovers_RemovesStagingAndTrash()
    {
        var appDir = _store.AppDirectory(CarLog);
        Directory.CreateDirectory(Path.Combine(appDir, ".staging-1234"));
        Directory.CreateDirectory(Path.Combine(appDir, ".trash-5678"));

        _store.CleanupLeftovers();

        Assert.Empty(Directory.GetDirectories(appDir));
    }

    [Theory]
    [InlineData("v1.0.0", "v1.0.0")]
    [InlineData("release/1.0", "release_1.0")]
    [InlineData(".hidden", "_.hidden")]
    public void SafeFolderName(string tag, string expected) => Assert.Equal(expected, AppStore.SafeFolderName(tag));
}

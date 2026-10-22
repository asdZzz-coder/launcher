using AppLauncher.Models;
using AppLauncher.Services;
using AppLauncher.ViewModels;

namespace AppLauncher.Tests;

/// <summary>開啟前選資料夾：命令列參數、最近開過的資料夾、選擇視窗的邏輯。</summary>
public class FolderPickerTests
{
    // ---------- 命令列參數 ----------

    [Theory]
    [InlineData(new[] { "--root", @"D:\文件" }, @"--root D:\文件")]
    [InlineData(new[] { "--root", @"D:\公司 文件" }, @"--root ""D:\公司 文件""")]
    [InlineData(new[] { "--root", @"\\nas\share\公司 文件\" }, @"--root ""\\nas\share\公司 文件\\""")]
    [InlineData(new[] { "--root", "" }, @"--root """"")]
    public void JoinArguments_QuotesSpacesAndTrailingBackslash(string[] args, string expected) =>
        Assert.Equal(expected, AppStore.JoinArguments(args));

    // ---------- 最近開過的資料夾 ----------

    [Fact]
    public void RememberFolder_PutsLatestFirstWithoutDuplicates()
    {
        var settings = new LauncherSettings();
        settings.RememberFolder("document", @"D:\A");
        settings.RememberFolder("document", @"\\nas\share");
        settings.RememberFolder("DOCUMENT", @"d:\a\");

        Assert.Equal([@"d:\a\", @"\\nas\share"], settings.GetRecentFolders("document"));
        Assert.Empty(settings.GetRecentFolders("other"));
    }

    [Fact]
    public void RememberFolder_KeepsAtMostTheLimit()
    {
        var settings = new LauncherSettings();
        for (var i = 0; i < LauncherSettings.MaxRecentFolders + 3; i++) settings.RememberFolder("a", $@"D:\{i}");
        var list = settings.GetRecentFolders("a");
        Assert.Equal(LauncherSettings.MaxRecentFolders, list.Count);
        Assert.Equal($@"D:\{LauncherSettings.MaxRecentFolders + 2}", list[0]);
    }

    [Fact]
    public void ForgetFolder_RemovesIt()
    {
        var settings = new LauncherSettings();
        settings.RememberFolder("a", @"D:\A");
        settings.RememberFolder("a", @"D:\B");
        settings.ForgetFolder("a", @"d:\a");
        Assert.Equal([@"D:\B"], settings.GetRecentFolders("a"));
    }

    [Fact]
    public void RecentFolders_SurviveSaveAndLoad()
    {
        var path = Path.Combine(Path.GetTempPath(), $"AppLauncherTests-{Guid.NewGuid():N}", "settings.json");
        try
        {
            var settings = new LauncherSettings();
            settings.RememberFolder("document", @"\\nas\公司文件");
            settings.Save(path);
            Assert.Equal([@"\\nas\公司文件"], LauncherSettings.Load(path).GetRecentFolders("DOCUMENT"));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch (IOException) { }
        }
    }

    // ---------- 選擇視窗 ----------

    [Fact]
    public void Picker_SelectsMostRecentFolder()
    {
        var vm = new FolderPickerViewModel("文件整理", [@"D:\A", @"D:\B"]);
        Assert.Equal(@"D:\A", vm.Selected?.Path);
        Assert.False(vm.IsEmpty);
    }

    [Fact]
    public async Task Picker_MarksMissingFoldersAndDisablesOpen()
    {
        var vm = new FolderPickerViewModel("文件整理", [@"D:\gone", @"D:\here"], exists: p => p == @"D:\here");
        await vm.CheckAllAsync();

        Assert.Equal(FolderState.Missing, vm.Choices[0].State);
        Assert.Equal(FolderState.Ready, vm.Choices[1].State);
        Assert.False(vm.CanOpen); // 最近的那個連不上
        vm.Selected = vm.Choices[1];
        Assert.True(vm.CanOpen);
    }

    [Fact]
    public async Task Picker_SlowFolderCountsAsMissingAfterTimeout()
    {
        var gate = new ManualResetEventSlim(false);
        var vm = new FolderPickerViewModel("文件整理", [@"\\nas\share"], exists: _ => { gate.Wait(); return true; });
        var started = DateTime.UtcNow;
        await vm.CheckAllAsync();
        gate.Set();

        Assert.Equal(FolderState.Missing, vm.Choices[0].State);
        Assert.True(DateTime.UtcNow - started < FolderPickerViewModel.CheckTimeout + TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Picker_AddMovesFolderToTopAndRemoveRemembersIt()
    {
        var vm = new FolderPickerViewModel("文件整理", [@"D:\A", @"D:\B"]);
        vm.Add(@"d:\b");
        Assert.Equal([@"d:\b", @"D:\A"], vm.Choices.Select(c => c.Path));
        Assert.Equal(@"d:\b", vm.Selected?.Path);
        Assert.True(vm.CanOpen);

        vm.Remove(vm.Choices[0]);
        Assert.Equal(@"D:\A", vm.Selected?.Path);
        Assert.Equal([@"d:\b"], vm.Removed);
    }

    [Fact]
    public void Picker_TextsDependOnPurpose()
    {
        var setting = new FolderPickerViewModel("文件整理", []);
        Assert.Equal("使用這個資料夾", setting.ConfirmText);
        Assert.Equal("「文件整理」要讀取的資料夾", setting.Title);
        var launching = new FolderPickerViewModel("文件整理", [], forLaunch: true);
        Assert.Equal("開啟", launching.ConfirmText);
        Assert.Equal("開啟「文件整理」", launching.Title);
    }

    // ---------- 卡片上的「資料夾」 ----------

    private static AppItemViewModel Card(AppDefinition definition, LauncherSettings settings) =>
        new(definition, new AppStore(Path.Combine(Path.GetTempPath(), "AppLauncherTests-" + Guid.NewGuid().ToString("N"))),
            new GitHubService(Path.Combine(Path.GetTempPath(), $"AppLauncherTests-{Guid.NewGuid():N}.json")),
            settings: settings);

    [Fact]
    public void Card_ShowsMostRecentFolder()
    {
        var settings = new LauncherSettings();
        var card = Card(new AppDefinition("document", "文件整理", "o/r", "x", "x.exe", FolderArg: "--root"), settings);
        Assert.True(card.UsesFolder);
        Assert.Null(card.Folder);
        Assert.StartsWith("尚未設定", card.FolderText);

        settings.RememberFolder("document", @"\\nas\share\公司文件");
        Assert.Equal(@"\\nas\share\公司文件", card.Folder);
        Assert.Equal("公司文件", card.FolderText);
    }

    [Fact]
    public void Card_WithoutFolderArgHidesFolderRow()
    {
        var settings = new LauncherSettings();
        settings.RememberFolder("carlog", @"D:\A");
        var card = Card(new AppDefinition("carlog", "車輛紀錄", "o/r", "x", "x.exe"), settings);
        Assert.False(card.UsesFolder);
        Assert.Null(card.Folder);
    }

    [Fact]
    public void Picker_UncPathIsNetwork()
    {
        Assert.True(new FolderChoice(@"\\nas\share").IsNetwork);
        Assert.Equal(@"\\nas\share\", new FolderChoice(@"\\nas\share\").Name); // 分享的根目錄顯示整個路徑
        Assert.Equal("公司文件", new FolderChoice(@"\\nas\share\公司文件").Name);
        Assert.Equal(@"D:\", new FolderChoice(@"D:\").Name);
    }
}

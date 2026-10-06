using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using AppLauncher.Models;
using AppLauncher.Services;

namespace AppLauncher.ViewModels;

/// <summary>
/// 啟動器自己的更新：查 launcher repo 的最新 Release，有新版時顯示提示列，
/// 使用者按「更新」後下載安裝檔（msi）、交給 Windows Installer 安裝，啟動器自己先關閉，
/// 安裝完成後安裝檔會自動重新開啟啟動器。
/// </summary>
public sealed class LauncherUpdateViewModel : ObservableObject
{
    /// <summary>把啟動器本身當成一個 APP 來查 Release。</summary>
    internal static readonly AppDefinition Self = new(
        "applauncher", "APP 啟動器", Catalog.LauncherRepo, "^AppLauncher-Setup.*\\.msi$", "AppLauncher.exe");

    private readonly GitHubService _github;
    private readonly Func<bool> _isAnyBusy;
    private readonly Action _cancelAll;
    private ReleaseInfo? _available;
    private bool _isDownloading;
    private double _progress;
    private string? _error;

    public LauncherUpdateViewModel(GitHubService github, Func<bool> isAnyBusy, Action cancelAll)
    {
        _github = github;
        _isAnyBusy = isAnyBusy;
        _cancelAll = cancelAll;
        UpdateCommand = new RelayCommand(() => _ = UpdateAsync(), () => Available != null && !IsDownloading);
    }

    /// <summary>目前執行中的版本。環境變數 APPLAUNCHER_VERSION 可假裝成其他版本（測試用）。</summary>
    public static Version CurrentVersion { get; } =
        VersionTag.Parse(Environment.GetEnvironmentVariable("APPLAUNCHER_VERSION"))
        ?? typeof(LauncherUpdateViewModel).Assembly.GetName().Version
        ?? new Version(0, 0);

    public ICommand UpdateCommand { get; }

    /// <summary>GitHub 上比目前新的版本；沒有新版時為 null。</summary>
    public ReleaseInfo? Available
    {
        get => _available;
        private set
        {
            if (!SetProperty(ref _available, value)) return;
            OnPropertyChanged(nameof(HasUpdate));
            OnPropertyChanged(nameof(Text));
            RelayCommand.Refresh();
        }
    }

    public bool HasUpdate => Available != null;

    public string Text => Available == null ? ""
        : $"APP 啟動器有新版本 {Available.Tag}（目前 v{CurrentVersion.ToString(3)}）";

    public bool IsDownloading
    {
        get => _isDownloading;
        private set { if (SetProperty(ref _isDownloading, value)) RelayCommand.Refresh(); }
    }

    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }

    public string? Error { get => _error; private set => SetProperty(ref _error, value); }

    public async Task CheckAsync()
    {
        try
        {
            var latest = await _github.GetLatestAsync(Self);
            Available = latest != null && VersionTag.IsNewer(latest.Tag, CurrentVersion.ToString()) ? latest : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException)
        {
            // 查不到啟動器的更新不影響其他功能，下次檢查再試
        }
    }

    private async Task UpdateAsync()
    {
        if (Available is not { } release) return;
        if (_isAnyBusy() && !Dialogs.Confirm("還有 APP 正在下載，更新啟動器會中斷下載。確定要現在更新嗎？")) return;

        IsDownloading = true;
        Error = null;
        Progress = 0;
        var path = Path.Combine(Path.GetTempPath(), "AppLauncher", $"AppLauncher-Setup-{release.Tag}.msi");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await _github.DownloadAsync(release.DownloadUrl, path, release.Size,
                new Progress<double>(p => Progress = Math.Max(0, p) * 100), CancellationToken.None);

            // 交給 Windows Installer。安裝檔會取代舊版，裝好後自動開啟新版啟動器。
            Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{path}\"") { UseShellExecute = true })?.Dispose();
            _cancelAll();
            Application.Current.Shutdown();
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception)
        {
            Error = $"更新失敗：{ex.Message}";
            IsDownloading = false;
        }
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using AppLauncher.Models;
using AppLauncher.Services;

namespace AppLauncher.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>同時下載的數量，避免六個 60MB 一起搶頻寬。</summary>
    private const int MaxParallelDownloads = 2;

    private readonly AppStore _store;
    private readonly GitHubService _github;
    private readonly DispatcherTimer _autoRefresh;
    private bool _isRefreshing;
    private string? _statusText;

    public MainViewModel(IReadOnlyList<AppDefinition> apps, AppStore store, GitHubService github)
    {
        _store = store;
        _github = github;
        Apps = apps.Select(a => new AppItemViewModel(a, store, github)).ToList();
        foreach (var app in Apps) app.PropertyChanged += OnAppChanged;

        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => !IsRefreshing);
        DownloadSelectedCommand = new RelayCommand(() => _ = DownloadAsync(Apps.Where(a => a.IsSelected)),
            () => Apps.Any(a => a.IsSelected && a.CanDownload));
        UpdateAllCommand = new RelayCommand(() => _ = DownloadAsync(Apps.Where(a => a.HasUpdate)),
            () => Apps.Any(a => a.HasUpdate && a.CanDownload));
        DeleteSelectedCommand = new RelayCommand(DeleteSelected,
            () => Apps.Any(a => a.IsSelected && a.IsInstalled && !a.IsBusy));
        OpenAppsFolderCommand = new RelayCommand(OpenAppsFolder);

        // 開著的時候每 30 分鐘自動檢查一次更新（有 ETag 快取，不會耗掉 GitHub 查詢次數）
        _autoRefresh = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _autoRefresh.Tick += (_, _) => _ = RefreshAsync();
    }

    public IReadOnlyList<AppItemViewModel> Apps { get; }

    public ICommand RefreshCommand { get; }
    public ICommand DownloadSelectedCommand { get; }
    public ICommand UpdateAllCommand { get; }
    public ICommand DeleteSelectedCommand { get; }
    public ICommand OpenAppsFolderCommand { get; }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set { if (SetProperty(ref _isRefreshing, value)) RelayCommand.Refresh(); }
    }

    public string? StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    public int UpdateCount => Apps.Count(a => a.HasUpdate);
    public int SelectedCount => Apps.Count(a => a.IsSelected);
    public bool IsAnyBusy => Apps.Any(a => a.IsBusy);

    /// <summary>全選勾選框：全選 true、全不選 false、部分選 null。</summary>
    public bool? AllSelected
    {
        get => SelectedCount == 0 ? false : SelectedCount == Apps.Count ? true : null;
        set
        {
            var select = value ?? false;
            foreach (var app in Apps) app.IsSelected = select;
        }
    }

    public async Task InitializeAsync()
    {
        await Task.Run(_store.CleanupLeftovers);
        foreach (var app in Apps) app.ReloadInstalled();
        await RefreshAsync();
        _autoRefresh.Start();
    }

    public async Task RefreshAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        StatusText = "正在檢查更新…";
        try
        {
            foreach (var app in Apps) app.ReloadInstalled();
            await Task.WhenAll(Apps.Select(a => a.CheckAsync()));
            _github.SaveCache();
            StatusText = $"上次檢查：{DateTime.Now:HH:mm}";
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private async Task DownloadAsync(IEnumerable<AppItemViewModel> apps)
    {
        var targets = apps.Where(a => a.CanDownload).ToList();
        using var gate = new SemaphoreSlim(MaxParallelDownloads);
        await Task.WhenAll(targets.Select(async app =>
        {
            await gate.WaitAsync();
            try { await app.DownloadAsync(); }
            finally { gate.Release(); }
        }));
    }

    private void DeleteSelected()
    {
        var targets = Apps.Where(a => a.IsSelected && a.IsInstalled && !a.IsBusy).ToList();
        if (targets.Count == 0) return;
        var names = string.Join("\n", targets.Select(a => $"・{a.Name}（{a.Versions.Count} 個版本）"));
        if (!Dialogs.Confirm($"確定要刪除以下 APP 的所有版本？\n\n{names}\n\n你在程式裡的資料不會被刪除。")) return;

        var deleted = targets.Count(a => a.DeleteAll(confirm: false));
        StatusText = deleted == targets.Count
            ? $"已刪除 {deleted} 個 APP"
            : $"已刪除 {deleted} 個 APP，{targets.Count - deleted} 個刪除失敗（請看各 APP 的訊息）";
    }

    private void OpenAppsFolder()
    {
        Directory.CreateDirectory(_store.Root);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_store.Root}\""))?.Dispose();
    }

    public void CancelAll()
    {
        foreach (var app in Apps) app.Cancel();
    }

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppItemViewModel.HasUpdate):
                OnPropertyChanged(nameof(UpdateCount));
                break;
            case nameof(AppItemViewModel.IsSelected):
                OnPropertyChanged(nameof(SelectedCount));
                OnPropertyChanged(nameof(AllSelected));
                RelayCommand.Refresh();
                break;
            case nameof(AppItemViewModel.IsBusy):
                OnPropertyChanged(nameof(IsAnyBusy));
                break;
        }
    }

    public void Dispose()
    {
        _autoRefresh.Stop();
        _github.Dispose();
    }
}

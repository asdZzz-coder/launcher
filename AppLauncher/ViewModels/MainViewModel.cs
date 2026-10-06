using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
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
    private readonly LauncherSettings _settings;
    private readonly string _settingsPath;
    private readonly DispatcherTimer _autoRefresh;
    private bool _isRefreshing;
    private string? _statusText;
    private string _filter = "All";

    public MainViewModel(IReadOnlyList<AppDefinition> apps, AppStore store, GitHubService github,
        LauncherSettings settings, string settingsPath)
    {
        _store = store;
        _github = github;
        _settings = settings;
        _settingsPath = settingsPath;
        Apps = new ObservableCollection<AppItemViewModel>(LauncherSettings.ApplyOrder(apps, settings.Order)
            .Select(a => new AppItemViewModel(a, store, github, MoveBy, CanMoveBy)));
        foreach (var app in Apps) app.PropertyChanged += OnAppChanged;
        AppsView = CollectionViewSource.GetDefaultView(Apps);
        AppsView.Filter = o => o is AppItemViewModel app && Filter switch
        {
            "Installed" => app.IsInstalled,
            "Updates" => app.HasUpdate,
            _ => true,
        };

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

    /// <summary>所有 APP，依使用者拖曳排好的順序。</summary>
    public ObservableCollection<AppItemViewModel> Apps { get; }

    /// <summary>Grid（方格）或 List（條列）。</summary>
    public string ViewMode
    {
        get => _settings.ViewMode;
        set
        {
            if (value != LauncherSettings.ListView) value = LauncherSettings.GridView;
            if (_settings.ViewMode == value) return;
            _settings.ViewMode = value;
            _settings.Save(_settingsPath);
            OnPropertyChanged();
        }
    }

    /// <summary>把 item 移到 target 的位置（拖曳時用）。</summary>
    public void MoveApp(AppItemViewModel item, AppItemViewModel target)
    {
        var from = Apps.IndexOf(item);
        var to = Apps.IndexOf(target);
        if (from < 0 || to < 0 || from == to) return;
        Apps.Move(from, to);
    }

    /// <summary>往前（-1）或往後（+1）移一格。篩選分頁中跳過看不到的 APP，移動的結果才看得出來。</summary>
    private void MoveBy(AppItemViewModel item, int delta)
    {
        var visible = AppsView.Cast<AppItemViewModel>().ToList();
        var index = visible.IndexOf(item);
        if (index < 0 || index + delta < 0 || index + delta >= visible.Count) return;
        MoveApp(item, visible[index + delta]);
        SaveOrder();
    }

    public bool CanMoveBy(AppItemViewModel item, int delta)
    {
        var visible = AppsView.Cast<AppItemViewModel>().ToList();
        var index = visible.IndexOf(item);
        return index >= 0 && index + delta >= 0 && index + delta < visible.Count;
    }

    public void SaveOrder()
    {
        _settings.Order = Apps.Select(a => a.Definition.Id).ToList();
        _settings.Save(_settingsPath);
    }

    /// <summary>依上方分頁（全部／已安裝／可更新）篩選後的清單。</summary>
    public ICollectionView AppsView { get; }

    /// <summary>目前的分頁：All、Installed、Updates。</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (!SetProperty(ref _filter, value)) return;
            AppsView.Refresh();
            OnPropertyChanged(nameof(IsViewEmpty));
            OnPropertyChanged(nameof(EmptyText));
        }
    }

    public bool IsViewEmpty => AppsView.IsEmpty;

    public string EmptyText => Filter switch
    {
        "Installed" => "還沒有下載任何 APP",
        "Updates" => "所有 APP 都是最新版",
        _ => "清單裡沒有 APP",
    };

    public int AllCount => Apps.Count;
    public int InstalledCount => Apps.Count(a => a.IsInstalled);

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
    public bool HasSelection => SelectedCount > 0;
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
        _ = Task.WhenAll(Apps.Select(a => a.LoadRepoIconAsync(LauncherPaths.IconCache)));
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
            case nameof(AppItemViewModel.IsInstalled):
                OnPropertyChanged(nameof(UpdateCount));
                OnPropertyChanged(nameof(InstalledCount));
                // 正在下載的 APP 不要因為狀態改變就從目前分頁消失，等下次切換分頁再篩選
                if (Filter != "All" && sender is AppItemViewModel { IsBusy: false })
                {
                    AppsView.Refresh();
                    OnPropertyChanged(nameof(IsViewEmpty));
                }
                break;
            case nameof(AppItemViewModel.IsSelected):
                OnPropertyChanged(nameof(SelectedCount));
                OnPropertyChanged(nameof(HasSelection));
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

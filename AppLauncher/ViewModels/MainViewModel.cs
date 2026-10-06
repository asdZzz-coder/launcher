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
        // 第一次使用：目前清單裡的 APP 都當作看過，不標示「新」
        if (_settings.SeenApps == null)
        {
            _settings.SeenApps = apps.Select(a => a.Id).ToList();
            _settings.Save(_settingsPath);
        }
        Apps = new ObservableCollection<AppItemViewModel>(LauncherSettings.ApplyOrder(apps, settings.Order).Select(CreateItem));
        LauncherUpdate = new LauncherUpdateViewModel(github, () => IsAnyBusy, CancelAll);
        DismissNewAppsCommand = new RelayCommand(DismissNewApps);
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

    /// <summary>啟動器本身的更新。</summary>
    public LauncherUpdateViewModel LauncherUpdate { get; }

    public string VersionText => $"版本 {LauncherUpdateViewModel.CurrentVersion.ToString(3)}";

    private AppItemViewModel CreateItem(AppDefinition definition)
    {
        var item = new AppItemViewModel(definition, _store, _github, MoveBy, CanMoveBy);
        item.PropertyChanged += OnAppChanged;
        return item;
    }

    // ---------- 線上 APP 清單 ----------

    /// <summary>線上清單新加入、還沒下載的 APP 數量。</summary>
    public int NewCount => Apps.Count(a => a.IsNew);

    public string NewAppsText => $"有 {NewCount} 個新的 APP：{string.Join("、", Apps.Where(a => a.IsNew).Select(a => a.Name))}";

    public ICommand DismissNewAppsCommand { get; }

    private void UpdateNewFlags()
    {
        var seen = new HashSet<string>(_settings.SeenApps ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var app in Apps) app.IsNew = !seen.Contains(app.Definition.Id) && !app.IsInstalled;
        OnPropertyChanged(nameof(NewCount));
        OnPropertyChanged(nameof(NewAppsText));
    }

    private void MarkSeen(IEnumerable<string> ids)
    {
        var seen = _settings.SeenApps ??= [];
        var added = false;
        foreach (var id in ids)
        {
            if (seen.Contains(id, StringComparer.OrdinalIgnoreCase)) continue;
            seen.Add(id);
            added = true;
        }
        if (added) _settings.Save(_settingsPath);
        UpdateNewFlags();
    }

    /// <summary>「知道了」：不再標示目前的新 APP。</summary>
    private void DismissNewApps() => MarkSeen(Apps.Where(a => a.IsNew).Select(a => a.Definition.Id));

    /// <summary>下載 GitHub 上的 APP 清單，套用新增、修改、移除。自訂清單（apps.json 放在本機）時不做。</summary>
    private async Task SyncCatalogAsync()
    {
        if (Catalog.HasOverride) return;
        var json = await _github.GetTextAsync(Catalog.RemoteUrl);
        if (json == null) return; // 連不上就沿用目前的清單
        IReadOnlyList<AppDefinition> definitions;
        try
        {
            definitions = Catalog.Parse(json);
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            StatusText = $"GitHub 上的 APP 清單格式有誤，先沿用目前的清單（{ex.Message}）";
            return;
        }
        Catalog.SaveRemoteCache(json);
        ApplyCatalog(definitions);
    }

    internal void ApplyCatalog(IReadOnlyList<AppDefinition> definitions)
    {
        var byId = definitions.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);

        // 從清單拿掉的 APP：沒裝的直接移除；有裝的留著，才能繼續開啟或刪除
        foreach (var app in Apps.ToList())
        {
            if (byId.TryGetValue(app.Definition.Id, out var definition))
                app.UpdateDefinition(definition);
            else if (!app.IsInstalled && !app.IsBusy)
            {
                app.PropertyChanged -= OnAppChanged;
                Apps.Remove(app);
            }
        }

        // 新加入的 APP 排在最後（之後使用者可以自己拖到想要的位置）
        foreach (var definition in definitions)
        {
            if (Apps.Any(a => string.Equals(a.Definition.Id, definition.Id, StringComparison.OrdinalIgnoreCase))) continue;
            var item = CreateItem(definition);
            item.ReloadInstalled();
            Apps.Add(item);
            _ = item.LoadRepoIconAsync(LauncherPaths.IconCache);
        }

        foreach (var app in Apps.Where(a => a.Icon == null)) _ = app.LoadRepoIconAsync(LauncherPaths.IconCache);
        UpdateNewFlags();
        OnPropertyChanged(nameof(AllCount));
        OnPropertyChanged(nameof(InstalledCount));
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(IsViewEmpty));
    }

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
        UpdateNewFlags();
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
            await SyncCatalogAsync();
            foreach (var app in Apps) app.ReloadInstalled();
            await Task.WhenAll(Apps.Select(a => a.CheckAsync()).Append(LauncherUpdate.CheckAsync()));
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
                // 新 APP 下載後就不再標示「新」
                if (sender is AppItemViewModel { IsNew: true, IsInstalled: true } installed)
                    MarkSeen([installed.Definition.Id]);
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

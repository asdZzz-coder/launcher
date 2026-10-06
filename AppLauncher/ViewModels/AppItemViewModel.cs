using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using AppLauncher.Models;
using AppLauncher.Services;

namespace AppLauncher.ViewModels;

/// <summary>版本下拉選單的一個選項。</summary>
public sealed record VersionOption(InstalledVersion Version, string Label)
{
    // 下拉選單收合時顯示的文字
    public override string ToString() => Label;
}

/// <summary>清單中的一個 APP：已下載的版本、GitHub 上的最新版、下載進度。</summary>
public sealed class AppItemViewModel : ObservableObject
{
    private readonly AppStore _store;
    private readonly GitHubService _github;
    private CancellationTokenSource? _cts;

    private VersionOption? _selectedVersion;
    private ReleaseInfo? _latest;
    private ImageSource? _icon;
    private ImageSource? _repoIcon;
    private string? _status;
    private bool _hasError;
    private bool _isBusy;
    private bool _isChecking;
    private bool _isIndeterminate;
    private double _progress;
    private bool _isSelected;
    private bool _isDragging;

    /// <param name="move">往前（-1）／往後（+1）移動這個 APP 的位置，由清單提供。</param>
    public AppItemViewModel(AppDefinition definition, AppStore store, GitHubService github,
        Action<AppItemViewModel, int>? move = null, Func<AppItemViewModel, int, bool>? canMove = null)
    {
        Definition = definition;
        _store = store;
        _github = github;

        MoveEarlierCommand = new RelayCommand(() => move?.Invoke(this, -1), () => canMove?.Invoke(this, -1) ?? false);
        MoveLaterCommand = new RelayCommand(() => move?.Invoke(this, 1), () => canMove?.Invoke(this, 1) ?? false);

        LaunchCommand = new RelayCommand(Launch, () => SelectedVersion != null);
        DownloadCommand = new RelayCommand(() => _ = DownloadAsync(), () => CanDownload);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        DeleteSelectedVersionCommand = new RelayCommand(() => DeleteSelectedVersion(), () => SelectedVersion != null && !IsBusy);
        DeleteAllCommand = new RelayCommand(() => DeleteAll(confirm: true), () => IsInstalled && !IsBusy);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => SelectedVersion != null);
        OpenReleasePageCommand = new RelayCommand(OpenReleasePage);
    }

    public AppDefinition Definition { get; }
    public string Name => Definition.Name;
    public string? Description => Definition.Description;
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";

    public ObservableCollection<VersionOption> Versions { get; } = [];

    public ICommand LaunchCommand { get; }
    public ICommand DownloadCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand DeleteSelectedVersionCommand { get; }
    public ICommand DeleteAllCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand OpenReleasePageCommand { get; }
    public ICommand MoveEarlierCommand { get; }
    public ICommand MoveLaterCommand { get; }

    /// <summary>正在被拖曳（卡片變半透明）。</summary>
    public bool IsDragging { get => _isDragging; set => SetProperty(ref _isDragging, value); }

    public VersionOption? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (SetProperty(ref _selectedVersion, value)) OnPropertyChanged(nameof(DeleteVersionText));
        }
    }

    public ReleaseInfo? Latest
    {
        get => _latest;
        private set
        {
            if (!SetProperty(ref _latest, value)) return;
            ReloadInstalled(); // 標籤的「最新／舊版」會跟著變
        }
    }

    public ImageSource? Icon { get => _icon; private set => SetProperty(ref _icon, value); }
    public string? Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool HasError { get => _hasError; private set => SetProperty(ref _hasError, value); }
    public bool IsChecking
    {
        get => _isChecking;
        private set { if (SetProperty(ref _isChecking, value)) NotifyBadge(); }
    }
    public bool IsIndeterminate { get => _isIndeterminate; private set => SetProperty(ref _isIndeterminate, value); }
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(CanDownload));
            RelayCommand.Refresh();
        }
    }

    public bool IsIdle => !IsBusy;

    public bool IsInstalled => Versions.Count > 0;
    public InstalledVersion? Newest => Versions.FirstOrDefault()?.Version;

    /// <summary>GitHub 上有比本機最新版更新的版本。舊版照樣可以開。</summary>
    public bool HasUpdate => Latest != null && Newest != null && VersionTag.IsNewer(Latest.Tag, Newest.Tag);

    public bool IsLatestInstalled =>
        Latest != null && Versions.Any(v => VersionTag.Compare(v.Version.Tag, Latest.Tag) == 0);

    public bool CanDownload => Latest != null && !IsLatestInstalled && !IsBusy;

    public string DownloadText => IsInstalled ? $"更新到 {Latest?.Tag}" : $"下載 {Latest?.Tag}";

    /// <summary>
    /// 卡片上的狀態標籤種類：Update（可更新）、Latest（已是最新）、Installed（已安裝但不知道最新版）、
    /// Available（尚未下載）、Checking（檢查中）、None。
    /// </summary>
    public string BadgeKind =>
        HasUpdate ? "Update"
        : IsLatestInstalled ? "Latest"
        : IsInstalled ? "Installed"
        : Latest != null ? "Available"
        : IsChecking ? "Checking"
        : "None";

    public string BadgeText => BadgeKind switch
    {
        "Update" => $"可更新 {Latest!.Tag}",
        "Latest" => $"已是最新 {Latest!.Tag}",
        "Installed" => $"已安裝 {Newest!.Tag}",
        "Available" => $"{Latest!.Tag} · {FormatSize(Latest.Size)}",
        "Checking" => "正在檢查…",
        _ => "",
    };

    /// <summary>滑鼠移到狀態標籤上時顯示的完整說明。</summary>
    public string? BadgeToolTip
    {
        get
        {
            var lines = new List<string>();
            if (IsInstalled) lines.Add($"已安裝：{string.Join("、", Versions.Select(v => v.Version.Tag))}");
            if (Latest != null)
                lines.Add($"GitHub 最新：{Latest.Tag}（{FormatSize(Latest.Size)}，{Latest.PublishedAt.LocalDateTime:yyyy/MM/dd} 發佈）");
            return lines.Count == 0 ? null : string.Join("\n", lines);
        }
    }

    public string UpdateText => $"更新到 {Latest?.Tag}";

    /// <summary>還沒有圖示時的底色，每個 APP 固定一種顏色。</summary>
    public Brush AvatarBrush => AvatarBrushes[(int)((uint)StableHash(Definition.Id) % AvatarBrushes.Length)];

    public string DeleteVersionText => SelectedVersion == null ? "刪除此版本" : $"刪除 {SelectedVersion.Version.Tag}";

    /// <summary>重新讀取本機已下載的版本，盡量保留目前選的版本。</summary>
    public void ReloadInstalled(string? selectTag = null)
    {
        selectTag ??= SelectedVersion?.Version.Tag;
        var installed = _store.GetInstalled(Definition);

        Versions.Clear();
        for (var i = 0; i < installed.Count; i++)
        {
            var v = installed[i];
            var isLatest = Latest != null ? VersionTag.Compare(v.Tag, Latest.Tag) >= 0 : i == 0;
            Versions.Add(new VersionOption(v, isLatest ? $"{v.Tag} 最新" : $"{v.Tag} 舊版"));
        }
        SelectedVersion = Versions.FirstOrDefault(o => o.Version.Tag == selectTag) ?? Versions.FirstOrDefault();
        Icon = (Newest != null ? IconLoader.FromExe(Newest.ExePath) : null) ?? _repoIcon;

        foreach (var name in new[] { nameof(IsInstalled), nameof(Newest), nameof(HasUpdate), nameof(IsLatestInstalled),
                                     nameof(CanDownload), nameof(DownloadText), nameof(UpdateText) })
            OnPropertyChanged(name);
        NotifyBadge();
        RelayCommand.Refresh();
    }

    /// <summary>從 repo 抓圖示，還沒下載的 APP 也能顯示真正的圖示。</summary>
    public async Task LoadRepoIconAsync(string iconDir)
    {
        var path = await _github.GetIconAsync(Definition, iconDir);
        _repoIcon = path == null ? null : IconLoader.FromFile(path);
        Icon ??= _repoIcon;
    }

    private void NotifyBadge()
    {
        OnPropertyChanged(nameof(BadgeKind));
        OnPropertyChanged(nameof(BadgeText));
        OnPropertyChanged(nameof(BadgeToolTip));
    }

    /// <summary>向 GitHub 查最新版。</summary>
    public async Task CheckAsync(CancellationToken ct = default)
    {
        if (IsBusy) return;
        IsChecking = true;
        try
        {
            Latest = await _github.GetLatestAsync(Definition, ct);
            if (Latest == null) SetStatus("GitHub 上找不到發佈的版本（repo 可能是私有的，或還沒發佈）", error: true);
            else if (HasError) SetStatus(null);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetStatus(ex.Message, error: true);
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>下載最新版。舊版保留不動。</summary>
    public async Task DownloadAsync()
    {
        var release = Latest;
        if (release == null || IsBusy) return;

        IsBusy = true;
        IsIndeterminate = false;
        Progress = 0;
        SetStatus($"正在下載 {release.Tag}…");
        _cts = new CancellationTokenSource();
        var tempDir = Path.Combine(Path.GetTempPath(), "AppLauncher");
        var tempFile = Path.Combine(tempDir, $"{Definition.Id}-{Guid.NewGuid():N}-{release.AssetName}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var progress = new Progress<double>(p =>
            {
                if (p < 0) { IsIndeterminate = true; return; }
                Progress = p * 100;
                SetStatus($"正在下載 {release.Tag}… {p:P0}");
            });
            await _github.DownloadAsync(release.DownloadUrl, tempFile, release.Size, progress, _cts.Token);

            IsIndeterminate = true;
            SetStatus("正在解壓縮…");
            var installed = await Task.Run(() => _store.Install(Definition, release.Tag, release.AssetName, tempFile));
            ReloadInstalled(selectTag: installed.Tag);
            SetStatus($"{release.Tag} 下載完成");
        }
        catch (OperationCanceledException)
        {
            SetStatus("已取消下載");
        }
        catch (Exception ex)
        {
            SetStatus($"下載失敗：{ex.Message}", error: true);
        }
        finally
        {
            try { File.Delete(tempFile); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _cts.Dispose();
            _cts = null;
            IsIndeterminate = false;
            IsBusy = false;
        }
    }

    public void Cancel() => _cts?.Cancel();

    private void Launch()
    {
        if (SelectedVersion is not { } option) return;
        try
        {
            AppStore.Launch(option.Version);
            SetStatus($"已開啟 {option.Version.Tag}");
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            SetStatus($"無法開啟：{ex.Message}", error: true);
        }
    }

    private void DeleteSelectedVersion()
    {
        if (SelectedVersion is not { } option) return;
        if (!Dialogs.Confirm($"確定要刪除「{Name}」的 {option.Version.Tag}？\n\n你在程式裡的資料不會被刪除。")) return;
        try
        {
            _store.Delete(option.Version);
            ReloadInstalled();
            SetStatus($"已刪除 {option.Version.Tag}");
        }
        catch (IOException ex)
        {
            SetStatus(ex.Message, error: true);
        }
    }

    /// <summary>刪除所有版本。批次刪除時由外面統一確認，所以 confirm 可關掉。</summary>
    public bool DeleteAll(bool confirm)
    {
        if (!IsInstalled || IsBusy) return false;
        if (confirm && !Dialogs.Confirm($"確定要刪除「{Name}」的所有版本？\n\n你在程式裡的資料不會被刪除。")) return false;
        try
        {
            _store.DeleteAll(Definition);
            SetStatus("已刪除所有版本");
            return true;
        }
        catch (IOException ex)
        {
            SetStatus(ex.Message, error: true);
            return false;
        }
        finally
        {
            ReloadInstalled();
        }
    }

    private void OpenFolder()
    {
        if (SelectedVersion is { } option)
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.GetDirectoryName(option.Version.ExePath)}\""))?.Dispose();
    }

    private void OpenReleasePage()
    {
        var url = Latest?.HtmlUrl is { Length: > 0 } html ? html : $"https://github.com/{Definition.Repo}/releases";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
    }

    private void SetStatus(string? text, bool error = false)
    {
        Status = text;
        HasError = error;
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";

    private static readonly Brush[] AvatarBrushes =
    [
        Gradient("#6366F1", "#8B5CF6"), Gradient("#0EA5E9", "#2563EB"), Gradient("#10B981", "#059669"),
        Gradient("#F59E0B", "#EA580C"), Gradient("#EC4899", "#DB2777"), Gradient("#14B8A6", "#0891B2"),
    ];

    private static Brush Gradient(string from, string to)
    {
        var brush = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(from),
            (Color)ColorConverter.ConvertFromString(to), 45);
        brush.Freeze();
        return brush;
    }

    /// <summary>string.GetHashCode 每次執行都不同，自己算一個固定的。</summary>
    private static int StableHash(string s) => s.Aggregate(17, (h, c) => unchecked(h * 31 + c));
}

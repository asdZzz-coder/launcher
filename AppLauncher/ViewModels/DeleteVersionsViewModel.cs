using System.IO;
using System.Windows.Input;
using AppLauncher.Models;

namespace AppLauncher.ViewModels;

/// <summary>「選擇要刪除的版本」視窗裡的一個版本。</summary>
public sealed class VersionChoice : ObservableObject
{
    private bool _isChecked;

    public VersionChoice(InstalledVersion version, bool isLatest, bool isRunning, long size)
    {
        Version = version;
        IsLatest = isLatest;
        IsRunning = isRunning;
        Size = size;
    }

    public InstalledVersion Version { get; }
    public string Tag => Version.Tag;
    public bool IsLatest { get; }
    public bool IsRunning { get; }
    public long Size { get; }

    /// <summary>開著的版本刪不掉（檔案被占用），先不給勾。</summary>
    public bool CanDelete => !IsRunning;

    public string Detail => IsRunning
        ? "開啟中，請先關閉程式才能刪除"
        : $"下載於 {Version.InstalledAt:yyyy/MM/dd} · {AppItemViewModel.FormatSize(Size)}";

    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value && CanDelete);
    }
}

/// <summary>勾選一個 APP 要刪除哪些版本。</summary>
public sealed class DeleteVersionsViewModel : ObservableObject
{
    /// <param name="versions">已下載的版本，新的在前。</param>
    public DeleteVersionsViewModel(string appName, IReadOnlyList<InstalledVersion> versions, Func<InstalledVersion, bool> isRunning)
    {
        AppName = appName;
        Choices = versions.Select((v, i) => new VersionChoice(v, i == 0, isRunning(v), DirectorySize(v.Directory))).ToList();
        foreach (var choice in Choices)
            choice.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(VersionChoice.IsChecked)) return;
                OnPropertyChanged(nameof(Selected));
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(DeleteText));
            };
        KeepLatestCommand = new RelayCommand(KeepLatest, () => Choices.Count > 1);
    }

    public string AppName { get; }
    public string Title => $"刪除「{AppName}」的版本";
    public IReadOnlyList<VersionChoice> Choices { get; }

    public IReadOnlyList<InstalledVersion> Selected => Choices.Where(c => c.IsChecked).Select(c => c.Version).ToList();
    public bool HasSelection => Choices.Any(c => c.IsChecked);

    public string DeleteText
    {
        get
        {
            var selected = Choices.Where(c => c.IsChecked).ToList();
            return selected.Count == 0
                ? "刪除"
                : $"刪除 {selected.Count} 個版本（{AppItemViewModel.FormatSize(selected.Sum(c => c.Size))}）";
        }
    }

    /// <summary>勾選最新版以外的所有版本。</summary>
    public ICommand KeepLatestCommand { get; }

    private void KeepLatest()
    {
        foreach (var choice in Choices) choice.IsChecked = !choice.IsLatest;
    }

    private static long DirectorySize(string directory)
    {
        try
        {
            return new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}

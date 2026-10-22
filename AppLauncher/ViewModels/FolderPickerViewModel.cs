using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;

namespace AppLauncher.ViewModels;

public enum FolderState { Checking, Ready, Missing }

/// <summary>「選擇資料夾」視窗裡的一個資料夾。</summary>
public sealed class FolderChoice(string path) : ObservableObject
{
    private FolderState _state = FolderState.Checking;

    public string Path { get; } = path;

    /// <summary>顯示用的名稱：資料夾名稱；磁碟根目錄或網路分享根目錄就顯示整個路徑。</summary>
    public string Name => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path)) is { Length: > 0 } name ? name : Path;

    /// <summary>NAS 或網路磁碟（\\伺服器\分享 或對應的磁碟機代號）。</summary>
    public bool IsNetwork => FolderPickerViewModel.IsNetworkPath(Path);

    public FolderState State
    {
        get => _state;
        set
        {
            if (!SetProperty(ref _state, value)) return;
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(CanOpen));
        }
    }

    public string? StateText => State switch
    {
        FolderState.Checking => "正在確認…",
        FolderState.Missing => IsNetwork ? "連不上（NAS 沒開機或網路沒連上）" : "找不到這個資料夾",
        _ => null,
    };

    public bool CanOpen => State != FolderState.Missing;
}

/// <summary>開啟 APP 前選要讀取哪個資料夾：最近開過的資料夾 + 選擇其他資料夾。</summary>
public sealed class FolderPickerViewModel : ObservableObject
{
    /// <summary>確認資料夾在不在最多等幾秒。連不上的 NAS 要等 Windows 逾時，可能好幾十秒。</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(6);

    private readonly Func<string, bool> _exists;
    private FolderChoice? _selected;

    /// <param name="recent">最近開過的資料夾，最近的在前。</param>
    /// <param name="exists">檢查資料夾是否存在（測試時可替換）。</param>
    public FolderPickerViewModel(string appName, IReadOnlyList<string> recent, Func<string, bool>? exists = null)
    {
        AppName = appName;
        _exists = exists ?? Directory.Exists;
        Choices = new ObservableCollection<FolderChoice>(recent.Select(p => Track(new FolderChoice(p))));
        Selected = Choices.FirstOrDefault();
        Choices.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
        RemoveCommand = new RelayCommand(o => { if (o is FolderChoice c) Remove(c); });
    }

    public string AppName { get; }
    public string Title => $"開啟「{AppName}」";

    public ObservableCollection<FolderChoice> Choices { get; }
    public bool IsEmpty => Choices.Count == 0;

    /// <summary>從清單移除的資料夾，關閉視窗後從記錄裡忘掉。</summary>
    public List<string> Removed { get; } = [];

    public ICommand RemoveCommand { get; }

    public FolderChoice? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            OnPropertyChanged(nameof(CanOpen));
            RelayCommand.Refresh();
        }
    }

    public bool CanOpen => Selected is { CanOpen: true };

    /// <summary>使用者用「選擇其他資料夾」挑的資料夾：放到最上面並選取（已在清單裡就移上來）。</summary>
    public void Add(string path)
    {
        var existing = Choices.FirstOrDefault(c => Services.LauncherSettings.SameFolder(c.Path, path));
        if (existing != null) Choices.Remove(existing);
        Removed.RemoveAll(p => Services.LauncherSettings.SameFolder(p, path));
        var choice = Track(new FolderChoice(path) { State = FolderState.Ready }); // 剛從對話框選的，一定在
        Choices.Insert(0, choice);
        Selected = choice;
    }

    /// <summary>資料夾確認完（例如發現連不上）時，「開啟」按鈕跟著更新。</summary>
    private FolderChoice Track(FolderChoice choice)
    {
        choice.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(FolderChoice.CanOpen) || choice != Selected) return;
            OnPropertyChanged(nameof(CanOpen));
            RelayCommand.Refresh();
        };
        return choice;
    }

    public void Remove(FolderChoice choice)
    {
        var index = Choices.IndexOf(choice);
        if (index < 0) return;
        Choices.RemoveAt(index);
        Removed.Add(choice.Path);
        if (Selected == choice) Selected = Choices.Count == 0 ? null : Choices[Math.Min(index, Choices.Count - 1)];
    }

    /// <summary>在背景確認每個資料夾還在不在；NAS 連不上時不會讓視窗卡住，等太久就當作連不上。</summary>
    public async Task CheckAllAsync()
    {
        await Task.WhenAll(Choices.Where(c => c.State == FolderState.Checking).ToList().Select(async choice =>
        {
            var check = Task.Run(() => _exists(choice.Path));
            var finished = await Task.WhenAny(check, Task.Delay(CheckTimeout));
            choice.State = finished == check && check.Result ? FolderState.Ready : FolderState.Missing;
        }));
    }

    internal static bool IsNetworkPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        try
        {
            var root = System.IO.Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return false; }
    }
}

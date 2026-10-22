using System.IO;
using System.Text.Json;
using AppLauncher.Models;

namespace AppLauncher.Services;

/// <summary>使用者的偏好：APP 的排列順序、方格或條列、淺色或深色。存在 %LocalAppData%\AppLauncher\settings.json。</summary>
public sealed class LauncherSettings
{
    public const string GridView = "Grid";
    public const string ListView = "List";

    /// <summary>APP 的 id，依顯示順序。</summary>
    public List<string> Order { get; set; } = [];

    public string ViewMode { get; set; } = GridView;

    /// <summary>System（跟著 Windows）、Light 或 Dark。</summary>
    public string Theme { get; set; } = ThemeManager.SystemTheme;

    /// <summary>
    /// 已經看過的 APP id。清單裡出現不在這裡的 APP 就標示「新」。
    /// null 表示第一次使用：當下清單裡的 APP 全部視為看過，不標示。
    /// </summary>
    public List<string>? SeenApps { get; set; }

    /// <summary>有 folderArg 的 APP 最近開過的資料夾：APP id → 資料夾（最近的在前）。</summary>
    public Dictionary<string, List<string>> RecentFolders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>每個 APP 最多記住幾個資料夾。</summary>
    public const int MaxRecentFolders = 8;

    public IReadOnlyList<string> GetRecentFolders(string appId) =>
        RecentFolders.TryGetValue(appId, out var list) ? list : [];

    /// <summary>把資料夾移到最前面（同一個資料夾不分大小寫只留一筆），超過上限的舊資料夾忘掉。</summary>
    public void RememberFolder(string appId, string folder)
    {
        var list = GetRecentFolders(appId)
            .Where(f => !SameFolder(f, folder))
            .Prepend(folder)
            .Take(MaxRecentFolders)
            .ToList();
        RecentFolders[appId] = list;
    }

    public void ForgetFolder(string appId, string folder)
    {
        if (RecentFolders.TryGetValue(appId, out var list)) list.RemoveAll(f => SameFolder(f, folder));
    }

    internal static bool SameFolder(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    public static LauncherSettings Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(path)) is { } settings)
            {
                if (settings.ViewMode != ListView) settings.ViewMode = GridView;
                settings.Theme = ThemeManager.Normalize(settings.Theme);
                settings.Order ??= [];
                // JSON 讀回來的字典是區分大小寫的，換成不分大小寫的
                var recent = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var (id, folders) in settings.RecentFolders ?? new())
                    if (folders != null) recent.TryAdd(id, folders.Where(f => !string.IsNullOrWhiteSpace(f)).ToList());
                settings.RecentFolders = recent;
                return settings;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new LauncherSettings();
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // 存不了只是下次恢復預設排列
    }

    /// <summary>依記住的順序排列；清單裡新加的 APP 排在最後（維持 apps.json 的先後）。</summary>
    public static IReadOnlyList<AppDefinition> ApplyOrder(IReadOnlyList<AppDefinition> apps, IReadOnlyList<string> order)
    {
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < order.Count; i++) rank.TryAdd(order[i], i);
        return apps
            .Select((app, index) => (app, index))
            .OrderBy(x => rank.TryGetValue(x.app.Id, out var r) ? r : int.MaxValue)
            .ThenBy(x => x.index)
            .Select(x => x.app)
            .ToList();
    }
}

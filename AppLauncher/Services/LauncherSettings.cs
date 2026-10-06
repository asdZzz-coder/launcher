using System.IO;
using System.Text.Json;
using AppLauncher.Models;

namespace AppLauncher.Services;

/// <summary>使用者的偏好：APP 的排列順序、方格或條列。存在 %LocalAppData%\AppLauncher\settings.json。</summary>
public sealed class LauncherSettings
{
    public const string GridView = "Grid";
    public const string ListView = "List";

    /// <summary>APP 的 id，依顯示順序。</summary>
    public List<string> Order { get; set; } = [];

    public string ViewMode { get; set; } = GridView;

    /// <summary>
    /// 已經看過的 APP id。清單裡出現不在這裡的 APP 就標示「新」。
    /// null 表示第一次使用：當下清單裡的 APP 全部視為看過，不標示。
    /// </summary>
    public List<string>? SeenApps { get; set; }

    public static LauncherSettings Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(path)) is { } settings)
            {
                if (settings.ViewMode != ListView) settings.ViewMode = GridView;
                settings.Order ??= [];
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

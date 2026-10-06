using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using AppLauncher.Models;

namespace AppLauncher.Services;

/// <summary>
/// APP 清單，來源優先順序：
/// 1. %LocalAppData%\AppLauncher\apps.json（自訂清單，存在就只用它，不讀線上清單）
/// 2. GitHub 上 launcher repo 的 AppLauncher/apps.json（線上清單）。每次檢查更新時下載，
///    存在 catalog.json，所以在 GitHub 上新增 APP，所有啟動器都會自動出現，不用重新安裝。
/// 3. 程式內建的 apps.json（第一次開啟、還沒連上 GitHub 時）
/// </summary>
public static partial class Catalog
{
    public const string LauncherRepo = "asdZzz-coder/launcher";
    public const string RemoteUrl = $"https://raw.githubusercontent.com/{LauncherRepo}/main/AppLauncher/apps.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string OverridePath => Path.Combine(LauncherPaths.Home, "apps.json");
    public static string RemoteCachePath => Path.Combine(LauncherPaths.Home, "catalog.json");

    /// <summary>使用者放了自訂清單時，不使用線上清單。</summary>
    public static bool HasOverride => File.Exists(OverridePath);

    public static IReadOnlyList<AppDefinition> Load()
    {
        if (HasOverride)
            return Parse(File.ReadAllText(OverridePath));
        try
        {
            if (File.Exists(RemoteCachePath))
                return Parse(File.ReadAllText(RemoteCachePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            // 快取壞掉就用內建的，下次檢查更新會重新下載
        }
        return LoadEmbedded();
    }

    public static IReadOnlyList<AppDefinition> LoadEmbedded()
    {
        using var stream = typeof(Catalog).Assembly.GetManifestResourceStream("AppLauncher.apps.json")
            ?? throw new InvalidOperationException("程式內找不到 apps.json");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public static void SaveRemoteCache(string json)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RemoteCachePath)!);
            File.WriteAllText(RemoteCachePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal static IReadOnlyList<AppDefinition> Parse(string json)
    {
        var apps = JsonSerializer.Deserialize<List<AppDefinition>>(json, Options) ?? [];
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
        {
            if (string.IsNullOrWhiteSpace(app.Id) || !IdPattern().IsMatch(app.Id))
                throw new FormatException($"「{app.Name}」的 id 只能用英數字與 -_");
            if (!ids.Add(app.Id))
                throw new FormatException($"id「{app.Id}」重複了");
            if (string.IsNullOrWhiteSpace(app.Repo) || app.Repo.Count(c => c == '/') != 1)
                throw new FormatException($"「{app.Name}」的 repo 要寫成 擁有者/名稱");
            if (string.IsNullOrWhiteSpace(app.Asset) || string.IsNullOrWhiteSpace(app.Exe))
                throw new FormatException($"「{app.Name}」缺少 asset 或 exe");
            try { _ = new Regex(app.Asset); }
            catch (ArgumentException) { throw new FormatException($"「{app.Name}」的 asset 規則運算式寫錯了"); }
        }
        return apps;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex IdPattern();
}

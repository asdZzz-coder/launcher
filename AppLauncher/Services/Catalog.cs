using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using AppLauncher.Models;

namespace AppLauncher.Services;

/// <summary>
/// 讀取 APP 清單。預設用程式內建的 apps.json；
/// 若 %LocalAppData%\AppLauncher\apps.json 存在就改用它，不用重新編譯也能增減 APP。
/// </summary>
public static partial class Catalog
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string OverridePath => Path.Combine(LauncherPaths.Home, "apps.json");

    public static IReadOnlyList<AppDefinition> Load()
    {
        if (File.Exists(OverridePath))
            return Parse(File.ReadAllText(OverridePath));

        using var stream = typeof(Catalog).Assembly.GetManifestResourceStream("AppLauncher.apps.json")
            ?? throw new InvalidOperationException("程式內找不到 apps.json");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
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

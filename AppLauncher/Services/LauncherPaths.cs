using System.IO;

namespace AppLauncher.Services;

public static class LauncherPaths
{
    // 環境變數 APPLAUNCHER_HOME 可指定其他資料夾（測試用）
    public static string Home =>
        Environment.GetEnvironmentVariable("APPLAUNCHER_HOME") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppLauncher");

    /// <summary>各 APP 的各版本放在 apps\&lt;id&gt;\&lt;版本&gt;。</summary>
    public static string AppsRoot => Path.Combine(Home, "apps");

    public static string GitHubCache => Path.Combine(Home, "github-cache.json");

    public static string IconCache => Path.Combine(Home, "icons");

    public static string Settings => Path.Combine(Home, "settings.json");
}

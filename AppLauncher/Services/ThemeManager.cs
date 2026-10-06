using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace AppLauncher.Services;

/// <summary>
/// 淺色／深色主題，標題列也一起變。可固定用淺色或深色，
/// 或跟著 Windows「設定 → 個人化 → 色彩」切換（預設）。
/// </summary>
public static partial class ThemeManager
{
    public const string SystemTheme = "System";
    public const string LightTheme = "Light";
    public const string DarkTheme = "Dark";

    private const int DwmUseImmersiveDarkMode = 20;
    private static Application? _app;

    public static bool IsDark { get; private set; }

    /// <summary>System（跟著 Windows）、Light 或 Dark。</summary>
    public static string Mode { get; private set; } = SystemTheme;

    public static string Normalize(string? mode) => mode is LightTheme or DarkTheme ? mode : SystemTheme;

    public static void Initialize(Application app, string? mode)
    {
        _app = app;
        Mode = Normalize(mode);
        Apply();
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General || Mode != SystemTheme) return;
            app.Dispatcher.BeginInvoke(Apply);
        };
    }

    public static void SetMode(string? mode)
    {
        Mode = Normalize(mode);
        Apply();
    }

    private static void Apply()
    {
        if (_app == null) return;
        var dark = Mode switch
        {
            LightTheme => false,
            DarkTheme => true,
            _ => SystemPrefersDark(),
        };
        IsDark = dark;
        var palette = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/{(dark ? "Dark" : "Light")}.xaml"),
        };
        // App.xaml 裡顏色表固定放在第一個，Controls.xaml 在後面用 DynamicResource 參照它
        _app.Resources.MergedDictionaries[0] = palette;

        foreach (Window window in _app.Windows) ApplyTitleBar(window);
    }

    /// <summary>深色模式時讓標題列也變深色（Windows 10 20H1 以上）。</summary>
    public static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var value = IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref value, sizeof(int));
    }

    private static bool SystemPrefersDark()
    {
        // 環境變數 APPLAUNCHER_THEME=light／dark 可強制指定（測試用）
        switch (Environment.GetEnvironmentVariable("APPLAUNCHER_THEME")?.ToLowerInvariant())
        {
            case "light": return false;
            case "dark": return true;
        }

        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

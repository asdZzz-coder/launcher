using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace AppLauncher.Services;

/// <summary>跟著 Windows「設定 → 個人化 → 色彩」切換淺色／深色，標題列也一起變。</summary>
public static partial class ThemeManager
{
    private const int DwmUseImmersiveDarkMode = 20;

    public static bool IsDark { get; private set; }

    public static void Initialize(Application app)
    {
        Apply(app, SystemPrefersDark());
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General) return;
            app.Dispatcher.BeginInvoke(() => Apply(app, SystemPrefersDark()));
        };
    }

    private static void Apply(Application app, bool dark)
    {
        IsDark = dark;
        var palette = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/{(dark ? "Dark" : "Light")}.xaml"),
        };
        // App.xaml 裡顏色表固定放在第一個，Controls.xaml 在後面用 DynamicResource 參照它
        app.Resources.MergedDictionaries[0] = palette;

        foreach (Window window in app.Windows) ApplyTitleBar(window);
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

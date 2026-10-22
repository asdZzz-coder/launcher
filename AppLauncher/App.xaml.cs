using System.Windows;
using System.Windows.Threading;
using AppLauncher.Services;
using AppLauncher.ViewModels;

namespace AppLauncher;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        var settings = LauncherSettings.Load(LauncherPaths.Settings);
        ThemeManager.Initialize(this, settings.Theme);

        // 同時開兩個啟動器會搶著下載、刪除同一個資料夾，所以只允許一個。
        // 用 APPLAUNCHER_HOME 指定測試資料夾時另外算一個，開著正式的啟動器也能測試。
        _singleInstance = new Mutex(true, SingleInstanceName(), out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("APP 啟動器已經開著了。", "APP 啟動器", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        IReadOnlyList<Models.AppDefinition> apps;
        try
        {
            apps = Catalog.Load();
        }
        catch (Exception ex)
        {
            Dialogs.Error($"APP 清單讀取失敗：\n{ex.Message}\n\n若有自訂清單，請檢查 {Catalog.OverridePath}");
            Shutdown();
            return;
        }

        var vm = new MainViewModel(apps, new AppStore(LauncherPaths.AppsRoot), new GitHubService(LauncherPaths.GitHubCache),
            settings, LauncherPaths.Settings);
        MainWindow = new MainWindow(vm);
        MainWindow.Show();
    }

    private static string SingleInstanceName()
    {
        var name = $"AppLauncher-{Environment.UserName}";
        if (Environment.GetEnvironmentVariable("APPLAUNCHER_HOME") is not { Length: > 0 } home) return name;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(home).ToUpperInvariant())))[..16];
        return name + "." + hash;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Dialogs.Error($"發生未預期的錯誤：\n{e.Exception.Message}");
        e.Handled = true;
    }
}

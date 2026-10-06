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

        // 同時開兩個啟動器會搶著下載、刪除同一個資料夾，所以只允許一個
        _singleInstance = new Mutex(true, $"AppLauncher-{Environment.UserName}", out var isFirst);
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

        var vm = new MainViewModel(apps, new AppStore(LauncherPaths.AppsRoot), new GitHubService(LauncherPaths.GitHubCache));
        MainWindow = new MainWindow(vm);
        MainWindow.Show();
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

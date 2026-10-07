using System.Windows;

namespace AppLauncher.ViewModels;

public static class Dialogs
{
    public static bool Confirm(string message, string title = "APP 啟動器") =>
        MessageBox.Show(Application.Current?.MainWindow!, message, title,
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    /// <summary>開「選擇要刪除的版本」視窗，按「刪除」回傳 true。</summary>
    public static bool ChooseVersionsToDelete(DeleteVersionsViewModel vm) =>
        new DeleteVersionsWindow(vm) { Owner = Application.Current?.MainWindow }.ShowDialog() == true;

    public static void Error(string message, string title = "APP 啟動器") =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}

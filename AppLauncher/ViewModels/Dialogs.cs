using System.Windows;

namespace AppLauncher.ViewModels;

public static class Dialogs
{
    public static bool Confirm(string message, string title = "APP 啟動器") =>
        MessageBox.Show(Application.Current?.MainWindow!, message, title,
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    public static void Error(string message, string title = "APP 啟動器") =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}

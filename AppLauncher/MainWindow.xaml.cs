using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AppLauncher.ViewModels;

namespace AppLauncher;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        Loaded += async (_, _) => await _vm.InitializeAsync();
    }

    /// <summary>「⋯」按鈕用左鍵也能打開選單。</summary>
    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_vm.IsAnyBusy && !Dialogs.Confirm("還有 APP 正在下載，確定要關閉嗎？\n下載到一半的檔案會被丟棄。"))
        {
            e.Cancel = true;
            return;
        }
        _vm.CancelAll();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.Dispose();
        base.OnClosed(e);
    }
}

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AppLauncher.Services;
using AppLauncher.ViewModels;

namespace AppLauncher;

public partial class MainWindow : Window
{
    /// <summary>卡片最小寬度（含左右間距），視窗越寬一排放越多張。</summary>
    private const double MinTileWidth = 250;

    public static readonly DependencyProperty TileColumnsProperty =
        DependencyProperty.Register(nameof(TileColumns), typeof(int), typeof(MainWindow), new PropertyMetadata(3));

    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        Loaded += async (_, _) => await _vm.InitializeAsync();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    public int TileColumns
    {
        get => (int)GetValue(TileColumnsProperty);
        set => SetValue(TileColumnsProperty, value);
    }

    private void TileScroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var usable = e.NewSize.Width - TileScroller.Padding.Left - TileScroller.Padding.Right;
        TileColumns = Math.Clamp((int)(usable / MinTileWidth), 1, 6);
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

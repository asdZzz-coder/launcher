using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
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

    // ---------- 拖曳調整順序 ----------
    // 拖曳時一經過其他 APP 就立刻交換位置（即時預覽），放開滑鼠才存檔。

    private Point _dragStart;
    private AppItemViewModel? _dragCandidate;

    private void Item_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 從按鈕、下拉選單、勾選框開始的按壓是要操作它們，不是拖曳
        _dragCandidate = IsOnControl(e.OriginalSource as DependencyObject, (DependencyObject)sender)
            ? null
            : (sender as FrameworkElement)?.DataContext as AppItemViewModel;
        _dragStart = e.GetPosition(this);
    }

    private void Item_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is not { } item || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _dragCandidate = null;
        item.IsDragging = true;
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(AppItemViewModel), item), DragDropEffects.Move);
        }
        finally
        {
            item.IsDragging = false;
            _vm.SaveOrder();
        }
    }

    private void Item_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (e.Data.GetData(typeof(AppItemViewModel)) is AppItemViewModel dragged &&
            (sender as FrameworkElement)?.DataContext is AppItemViewModel target)
        {
            e.Effects = DragDropEffects.Move;
            if (dragged != target) _vm.MoveApp(dragged, target);
        }
        e.Handled = true;
    }

    private void Item_Drop(object sender, DragEventArgs e) => e.Handled = true;

    /// <summary>拖到清單上下邊緣時自動捲動。</summary>
    private void TileScroller_PreviewDragOver(object sender, DragEventArgs e)
    {
        const double edge = 48, step = 12;
        var y = e.GetPosition(TileScroller).Y;
        if (y < edge) TileScroller.ScrollToVerticalOffset(TileScroller.VerticalOffset - step);
        else if (y > TileScroller.ActualHeight - edge) TileScroller.ScrollToVerticalOffset(TileScroller.VerticalOffset + step);
    }

    private static bool IsOnControl(DependencyObject? element, DependencyObject container)
    {
        while (element != null && element != container)
        {
            if (element is ButtonBase or ComboBox or ScrollBar or TextBoxBase) return true;
            element = element is Visual or Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return false;
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

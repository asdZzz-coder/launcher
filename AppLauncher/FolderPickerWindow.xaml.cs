using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AppLauncher.Services;
using AppLauncher.ViewModels;
using Microsoft.Win32;

namespace AppLauncher;

/// <summary>開啟 APP 前選擇要讀取的資料夾。按「開啟」時 DialogResult 為 true，選的資料夾在 FolderPickerViewModel.Selected。</summary>
public partial class FolderPickerWindow : Window
{
    private readonly FolderPickerViewModel _vm;

    public FolderPickerWindow(FolderPickerViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        Loaded += async (_, _) =>
        {
            if (_vm.IsEmpty) Browse(); // 第一次開：直接請使用者挑資料夾
            await _vm.CheckAllAsync();
        };
    }

    private void Browse_Click(object sender, RoutedEventArgs e) => Browse();

    private void Browse()
    {
        // 不預設打開 NAS 上的資料夾：NAS 連不上時，選擇視窗本身會卡住
        var dialog = new OpenFolderDialog { Title = $"選擇「{_vm.AppName}」要讀取的資料夾" };
        if (_vm.Selected is { State: FolderState.Ready, IsNetwork: false } current) dialog.InitialDirectory = current.Path;
        if (dialog.ShowDialog(this) == true) _vm.Add(dialog.FolderName);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.CanOpen) DialogResult = true;
    }

    private void FolderList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 雙擊在「移除」按鈕上不算
        for (var d = e.OriginalSource as DependencyObject; d != null && d != FolderList; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
        {
            if (d is Button) return;
            if (d is ListBoxItem)
            {
                if (_vm.CanOpen) DialogResult = true;
                return;
            }
        }
    }
}

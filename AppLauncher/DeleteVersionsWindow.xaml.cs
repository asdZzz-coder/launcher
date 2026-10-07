using System.Windows;
using AppLauncher.Services;
using AppLauncher.ViewModels;

namespace AppLauncher;

/// <summary>勾選要刪除的版本。按「刪除」時 DialogResult 為 true，勾選結果在 DeleteVersionsViewModel.Selected。</summary>
public partial class DeleteVersionsWindow : Window
{
    public DeleteVersionsWindow(DeleteVersionsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}

using System.ComponentModel;
using System.Windows;
using MyUninstaller.App.ViewModels;

namespace MyUninstaller.App.Views;

public partial class WingetWindow : ThemedWindow
{
    private readonly WingetViewModel _viewModel;

    public WingetWindow(WingetViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
    }

    /// <summary>Daftar dimuat setelah jendela tampil (winget butuh beberapa detik).</summary>
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_viewModel.RefreshCommand.CanExecute(null))
        {
            _viewModel.RefreshCommand.Execute(null);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosing(e);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

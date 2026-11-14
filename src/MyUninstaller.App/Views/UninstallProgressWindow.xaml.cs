using System.ComponentModel;
using MyUninstaller.App.ViewModels;

namespace MyUninstaller.App.Views;

public partial class UninstallProgressWindow : ThemedWindow
{
    private readonly UninstallSessionViewModel _viewModel;

    public UninstallProgressWindow(UninstallSessionViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        viewModel.RequestClose += (_, _) => Close();
        Loaded += async (_, _) => await viewModel.RunAsync();
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Selama penghapus instalan berjalan, menutup jendela = minta Hentikan (dengan konfirmasi).
        if (_viewModel.IsRunning)
        {
            e.Cancel = true;
            _viewModel.StopCommand.Execute(null);
        }
    }
}

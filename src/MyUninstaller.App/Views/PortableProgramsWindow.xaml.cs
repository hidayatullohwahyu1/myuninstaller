using System.ComponentModel;
using System.Windows;
using MyUninstaller.App.ViewModels;

namespace MyUninstaller.App.Views;

public partial class PortableProgramsWindow : ThemedWindow
{
    private readonly PortableProgramsViewModel _viewModel;

    public PortableProgramsWindow(PortableProgramsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
    }

    /// <summary>Pemindaian dimulai saat jendela tampil (bukan di konstruktor) agar jendela langsung muncul.</summary>
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_viewModel.ScanCommand.CanExecute(null))
        {
            _viewModel.ScanCommand.Execute(null);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosing(e);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

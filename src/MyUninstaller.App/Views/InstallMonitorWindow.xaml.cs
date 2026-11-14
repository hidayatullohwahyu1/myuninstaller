using System.ComponentModel;
using System.Windows;
using MyUninstaller.App.ViewModels;

namespace MyUninstaller.App.Views;

public partial class InstallMonitorWindow : ThemedWindow
{
    private readonly InstallMonitorViewModel _viewModel;

    public InstallMonitorWindow(InstallMonitorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
    }

    private void Item_Toggled(object sender, RoutedEventArgs e) => _viewModel.RefreshCanSave();

    protected override void OnClosing(CancelEventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosing(e);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

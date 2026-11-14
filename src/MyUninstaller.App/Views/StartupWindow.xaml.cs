using System.Windows;
using MyUninstaller.App.ViewModels;

namespace MyUninstaller.App.Views;

public partial class StartupWindow : ThemedWindow
{
    public StartupWindow(StartupViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

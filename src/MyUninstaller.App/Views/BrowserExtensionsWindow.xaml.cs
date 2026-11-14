using System.Windows;
using MyUninstaller.App.ViewModels;

namespace MyUninstaller.App.Views;

public partial class BrowserExtensionsWindow : ThemedWindow
{
    public BrowserExtensionsWindow(BrowserExtensionsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

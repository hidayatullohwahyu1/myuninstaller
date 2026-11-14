using System.ComponentModel;
using System.Windows;
using MyUninstaller.App.ViewModels;

namespace MyUninstaller.App.Views;

public partial class HistoryWindow : ThemedWindow
{
    private readonly HistoryViewModel _viewModel;

    public HistoryWindow(HistoryViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        Closing += OnClosing;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Jangan tutup di tengah pemulihan.
        if (_viewModel.IsWorking)
        {
            e.Cancel = true;
        }
    }
}

using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using MyUninstaller.App.Converters;
using MyUninstaller.App.Localization;
using MyUninstaller.App.ViewModels;

namespace MyUninstaller.App.Views;

public partial class LeftoverResultsWindow : ThemedWindow
{
    private readonly LeftoverResultsViewModel _viewModel;

    public LeftoverResultsWindow(LeftoverResultsViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        // Sebelum dibersihkan: "Lewati". Sesudahnya: "Tutup".
        SkipButton.SetBinding(ContentProperty, new Binding(nameof(LeftoverResultsViewModel.IsDone))
        {
            Converter = new BoolToTextConverter(Strings.Get("Common_Close"), Strings.Get("Leftovers_Skip")),
        });

        viewModel.RequestClose += (_, _) => Close();
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Jangan tutup di tengah pemindahan ke karantina.
        if (_viewModel.IsWorking)
        {
            e.Cancel = true;
        }
    }
}

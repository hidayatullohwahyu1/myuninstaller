using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using MyUninstaller.App.Interop;
using MyUninstaller.App.Localization;
using MyUninstaller.App.Services;
using MyUninstaller.App.ViewModels;
using MyUninstaller.Core.Abstractions;
using MyUninstaller.Core.Settings;

namespace MyUninstaller.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ThemeService _theme;
    private readonly ISettingsStore _settingsStore;

    public MainWindow(MainViewModel viewModel, ThemeService theme, ISettingsStore settingsStore)
    {
        _viewModel = viewModel;
        _theme = theme;
        _settingsStore = settingsStore;

        InitializeComponent();
        DataContext = viewModel;

        // "Pembersih" belum ada di daftar fitur PRD; tombolnya tetap nonaktif.

        // Ctrl+F: fokus ke kotak cari.
        InputBindings.Add(new KeyBinding(new FocusSearchCommand(this), Key.F, ModifierKeys.Control));

        // Saat berganti tampilan, jumlah terpilih mengikuti daftar yang sekarang terlihat.
        IconList.IsVisibleChanged += ProgramList_IsVisibleChanged;
        DetailsList.IsVisibleChanged += ProgramList_IsVisibleChanged;

        // Program dipilih dari Mode Pemburu: gulir sampai terlihat.
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedProgram) && viewModel.SelectedProgram is { } selected)
            {
                (viewModel.IsIconView ? (ListBox)IconList : DetailsList).ScrollIntoView(selected);
            }
        };

        SourceInitialized += OnSourceInitialized;
        // Daftar dimuat SETELAH frame pertama tergambar: jendela langsung muncul, lalu daftar menyusul.
        ContentRendered += OnContentRendered;
        DetailsList.SizeChanged += (_, _) => FitNameColumn();
        Closing += OnClosing;
        _theme.ThemeChanged += OnThemeChanged;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        RestorePlacement();
        ApplyTitleBarTheme();
    }

    private async void OnContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnContentRendered;
        try
        {
            await _viewModel.RefreshCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            // Galat sudah ditangani di ViewModel; ini hanya jaring pengaman untuk async void.
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    /// <summary>
    /// Kolom Nama mengisi sisa lebar tabel (kolom lain tetap), jadi tidak ada bilah gulir horizontal dan nama tidak
    /// terpotong saat jendela dilebarkan.
    /// </summary>
    private void FitNameColumn()
    {
        if (DetailsList.View is not GridView grid || grid.Columns.Count == 0 || DetailsList.ActualWidth <= 0)
        {
            return;
        }

        const double ScrollBarAndPadding = 36;
        double others = grid.Columns.Skip(1).Sum(c => c.ActualWidth);
        grid.Columns[0].Width = Math.Max(200, DetailsList.ActualWidth - others - ScrollBarAndPadding);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _theme.ThemeChanged -= OnThemeChanged;
        SavePlacement();
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyTitleBarTheme();

    private void ApplyTitleBarTheme() =>
        NativeMethods.SetDarkTitleBar(new WindowInteropHelper(this).Handle, _theme.IsDark);

    // ---------- Posisi dan ukuran jendela ----------

    private void RestorePlacement()
    {
        if (_settingsStore.Load().MainWindow is not { } p)
        {
            return;
        }

        double width = Math.Max(p.Width, MinWidth);
        double height = Math.Max(p.Height, MinHeight);

        // Pastikan jendela masih terlihat (misalnya monitor kedua sudah dilepas).
        var screen = new Rect(
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var titleBar = new Rect(p.Left, p.Top, width, 40);
        if (!screen.IntersectsWith(titleBar))
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = p.Left;
        Top = p.Top;
        Width = width;
        Height = height;
        if (p.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void SavePlacement()
    {
        try
        {
            Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            if (bounds.IsEmpty)
            {
                return;
            }

            var settings = _settingsStore.Load() with
            {
                MainWindow = new WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized),
            };
            _settingsStore.Save(settings);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // Tidak fatal: posisi jendela hanya tidak diingat.
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    // ---------- Interaksi ----------

    private void ProgramList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox list && list.IsVisible)
        {
            _viewModel.UpdateSelection(list.SelectedItems);
        }
    }

    private void ProgramList_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is ListBox { IsVisible: true } list)
        {
            _viewModel.UpdateSelection(list.SelectedItems);
        }
    }

    private void ProgramList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Klik ganda pada item (bukan area kosong) = Hapus instalan, seperti Your Uninstaller!.
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement((ItemsControl)sender, source) is ListBoxItem
            && _viewModel.UninstallCommand.CanExecute(null))
        {
            _viewModel.UninstallCommand.Execute(null);
        }
    }

    private void DetailsHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader { Tag: ProgramSortKey key })
        {
            _viewModel.SortByCommand.Execute(key);
        }
    }

    private void SortButton_Click(object sender, RoutedEventArgs e)
    {
        if (SortButton.ContextMenu is { } menu)
        {
            menu.PlacementTarget = SortButton;
            menu.IsOpen = true;
        }
    }

    private void SortDescending_Click(object sender, RoutedEventArgs e) =>
        _viewModel.SortDescending = !_viewModel.SortDescending;

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                _viewModel.ClearSearchCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Down:
                // Panah bawah dari kotak cari langsung ke daftar.
                ListBox list = _viewModel.IsIconView ? IconList : DetailsList;
                if (list.Items.Count > 0)
                {
                    list.SelectedIndex = Math.Max(list.SelectedIndex, 0);
                    list.UpdateLayout();
                    (list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex) as UIElement)?.Focus();
                    e.Handled = true;
                }

                break;
        }
    }

    // ---------- Jatuhkan pintasan/exe ke jendela = Mode Pemburu ----------

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            _viewModel.HuntFile(files[0]);
        }
    }

    private sealed class FocusSearchCommand(MainWindow window) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
            window.SearchBox.Focus();
            window.SearchBox.SelectAll();
        }
    }
}

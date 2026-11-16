using System.Collections;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyUninstaller.App.Localization;
using MyUninstaller.App.Services;
using MyUninstaller.App.Views;
using MyUninstaller.Core.Abstractions;
using MyUninstaller.Core.Browsers;
using MyUninstaller.Core.Formatting;
using MyUninstaller.Core.Leftovers;
using MyUninstaller.Core.Listing;
using MyUninstaller.Core.Models;
using MyUninstaller.Core.Monitoring;
using MyUninstaller.Core.Portable;
using MyUninstaller.Core.Scanning;
using MyUninstaller.Core.Settings;
using MyUninstaller.Core.Startup;
using MyUninstaller.Core.Usage;
using MyUninstaller.Core.Winget;

namespace MyUninstaller.App.ViewModels;

/// <summary>ViewModel jendela utama: daftar program, pencarian, urutan, tampilan, dan panel info.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IProgramScanner _scanner;
    private readonly InitialScan _initialScan;
    private readonly ISettingsStore _settingsStore;
    private readonly IClock _clock;
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;
    private readonly IconLoader _icons;
    private readonly Lazy<UninstallWorkflow> _uninstall;
    private readonly HunterProbe _hunter;
    private readonly Lazy<HistoryService> _history;
    private readonly IStartupRepository _startup;
    private readonly IUsageHistory _usage;
    private readonly IBrowserExtensionReader _extensions;
    private readonly Lazy<BrowserExtensionService> _extensionService;
    private readonly IPortableProgramScanner _portableScanner;
    private readonly Lazy<PortableProgramService> _portableService;
    private readonly Lazy<StartupCleanupService> _startupCleanup;
    private readonly IWingetClient _winget;
    private readonly ISystemSnapshotter _snapshotter;
    private readonly IInstallTraceStore _traces;
    private readonly ICachedFolderSizes _folderSizes;
    private readonly InstallLocationRules _locationRules;
    private readonly ILogger<MainViewModel> _logger;
    private readonly BulkObservableCollection<ProgramItemViewModel> _programs = [];

    private AppSettings _settings;
    private CancellationTokenSource? _loadCts;
    private IReadOnlyList<ProgramItemViewModel> _selectedPrograms = [];
    private string? _notice;
    private bool _initializing = true;

    public MainViewModel(
        IProgramScanner scanner,
        InitialScan initialScan,
        ISettingsStore settingsStore,
        IClock clock,
        IDialogService dialogs,
        IShellService shell,
        IconLoader icons,
        Lazy<UninstallWorkflow> uninstall,
        HunterProbe hunter,
        Lazy<HistoryService> history,
        IStartupRepository startup,
        IUsageHistory usage,
        IBrowserExtensionReader extensions,
        Lazy<BrowserExtensionService> extensionService,
        IPortableProgramScanner portableScanner,
        Lazy<PortableProgramService> portableService,
        Lazy<StartupCleanupService> startupCleanup,
        IWingetClient winget,
        ISystemSnapshotter snapshotter,
        IInstallTraceStore traces,
        ICachedFolderSizes folderSizes,
        InstallLocationRules locationRules,
        ILogger<MainViewModel> logger)
    {
        _scanner = scanner;
        _initialScan = initialScan;
        _settingsStore = settingsStore;
        _clock = clock;
        _dialogs = dialogs;
        _shell = shell;
        _icons = icons;
        _uninstall = uninstall;
        _hunter = hunter;
        _history = history;
        _startup = startup;
        _usage = usage;
        _extensions = extensions;
        _extensionService = extensionService;
        _portableScanner = portableScanner;
        _portableService = portableService;
        _startupCleanup = startupCleanup;
        _winget = winget;
        _snapshotter = snapshotter;
        _traces = traces;
        _folderSizes = folderSizes;
        _locationRules = locationRules;
        _logger = logger;

        // Layanan hapus instalan/riwayat (jurnal SQLite, karantina, ...) dibuat saat pertama dipakai, bukan saat
        // aplikasi dibuka: jendela utama tampil lebih cepat.
        ProgramsView = new ListCollectionView(_programs) { Filter = FilterProgram };

        _settings = settingsStore.Load();
        ViewMode = _settings.ViewMode;
        SortKey = _settings.SortKey;
        SortDescending = _settings.SortDescending;
        ShowSystemComponents = _settings.ShowSystemComponents;
        _initializing = false;

        ApplySort();
        UpdateStatus();
    }

    /// <summary>Tampilan yang sudah disaring dan diurutkan; dipakai kisi ikon dan tabel rincian.</summary>
    public ListCollectionView ProgramsView { get; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand), nameof(OpenWebsiteCommand), nameof(CopyNameCommand), nameof(SearchWebCommand), nameof(OpenRegistryCommand), nameof(RemoveBrokenEntryCommand))]
    public partial ProgramItemViewModel? SelectedProgram { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIconView), nameof(IsDetailsView))]
    public partial ProgramViewMode ViewMode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSortByName), nameof(IsSortByPublisher), nameof(IsSortBySize), nameof(IsSortByInstallDate))]
    public partial ProgramSortKey SortKey { get; set; }

    [ObservableProperty]
    public partial bool SortDescending { get; set; }

    /// <summary>Tampilkan juga komponen sistem, pembaruan, dan entri tanpa uninstaller.</summary>
    [ObservableProperty]
    public partial bool ShowSystemComponents { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyPropertyChangedFor(nameof(IsFirstLoad))]
    public partial bool IsLoading { get; set; }

    /// <summary>Daftar belum pernah terisi dan sedang dimuat: area utama menampilkan indikator memuat.</summary>
    public bool IsFirstLoad => IsLoading && _programs.Count == 0;

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? NoticeText { get; set; }

    [ObservableProperty]
    public partial string? EmptyText { get; set; }

    /// <summary>Ringkasan saat lebih dari satu program dipilih.</summary>
    [ObservableProperty]
    public partial string? MultiSelectionText { get; set; }

    public bool IsIconView
    {
        get => ViewMode == ProgramViewMode.Icons;
        set
        {
            if (value)
            {
                ViewMode = ProgramViewMode.Icons;
            }
        }
    }

    public bool IsDetailsView
    {
        get => ViewMode == ProgramViewMode.Details;
        set
        {
            if (value)
            {
                ViewMode = ProgramViewMode.Details;
            }
        }
    }

    public bool IsSortByName => SortKey == ProgramSortKey.Name;

    public bool IsSortByPublisher => SortKey == ProgramSortKey.Publisher;

    public bool IsSortBySize => SortKey == ProgramSortKey.Size;

    public bool IsSortByInstallDate => SortKey == ProgramSortKey.InstallDate;

    public bool HasSelection => _selectedPrograms.Count > 0;

    public int SelectedCount => _selectedPrograms.Count;

    /// <summary>Dipanggil View saat pilihan berubah (ListBox mendukung banyak pilihan).</summary>
    public void UpdateSelection(IList selectedItems)
    {
        _selectedPrograms = [.. selectedItems.OfType<ProgramItemViewModel>()];
        _notice = null;
        MultiSelectionText = _selectedPrograms.Count > 1
            ? Strings.Format("Info_MultipleSelected", IndonesianFormat.Count(_selectedPrograms.Count), IndonesianFormat.Size(_selectedPrograms.Sum(p => p.SizeBytes)))
            : null;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedCount));
        UninstallCommand.NotifyCanExecuteChanged();
        ForceUninstallCommand.NotifyCanExecuteChanged();
        UpdateStatus();
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = _loadCts = new CancellationTokenSource();

        IsLoading = true;
        StatusText = Strings.Get("Status_Loading");
        string? previousId = SelectedProgram?.Model.Id;

        try
        {
            // Pemindaian selalu di latar belakang agar UI tidak membeku.
            DateOnly today = _clock.Today;

            // Hasil sementara (program Win32) ditampilkan dulu; aplikasi Store menyusul.
            var partial = new Progress<IReadOnlyList<InstalledProgram>>(programs =>
            {
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                _programs.ReplaceAll(programs.Select(program => new ProgramItemViewModel(program, today)));
                OnPropertyChanged(nameof(IsFirstLoad));
                _notice = Strings.Get("Status_LoadingStore");
                UpdateStatus(evenWhileLoading: true);
            });

            // Pemuatan pertama memakai pemindaian yang sudah dimulai saat aplikasi dibuka (InitialScan).
            var programs = _initialScan.Take() is { } early
                ? await early.WaitAsync(cts.Token)
                : await Task.Run(() => _scanner.ScanAsync(partial, cts.Token), cts.Token);

            var items = programs.Select(program => new ProgramItemViewModel(program, today)).ToList();
            _programs.ReplaceAll(items);
            OnPropertyChanged(nameof(IsFirstLoad));

            SelectedProgram = previousId is null ? null : _programs.FirstOrDefault(p => p.Model.Id == previousId);
            _notice = null;
            _logger.LogInformation("Daftar program dimuat: {Count} entri", programs.Count);

            // Ikon dan ukuran folder menyusul di latar belakang; daftar sudah bisa dipakai.
            _ = LoadDetailsAsync(items, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Diganti pemindaian yang lebih baru.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gagal memuat daftar program");
            _notice = Strings.Get("Status_LoadFailed");
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                IsLoading = false;
                UpdateStatus();
                _firstLoad.TrySetResult();
            }
        }
    }

    private bool CanRefresh() => !IsLoading;

    /// <summary>Selesai saat pemuatan daftar pertama berakhir (berhasil atau gagal).</summary>
    private readonly TaskCompletionSource _firstLoad = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Menu klik kanan Explorer (versi 2.0): kenali program pemilik .exe/.lnk, lalu buka dialog hapus instalan biasa
    /// (konfirmasi dan pilihan mode tetap di tangan pengguna). Bila tidak dikenali, beri tahu saja.
    /// </summary>
    public async Task UninstallFromShellAsync(string path)
    {
        await _firstLoad.Task;
        while (IsLoading)
        {
            // Penyegaran lain sedang berjalan: tunggu daftar terbaru.
            await Task.Delay(200);
        }

        var result = _hunter.IdentifyFile(path, AllModels());
        if (result.Program is not { } program)
        {
            _logger.LogInformation("Menu klik kanan: pemilik {Path} tidak dikenali", path);
            _dialogs.ShowInfo(Strings.Get("AppName"), Strings.Format("Shell_NotFound", Path.GetFileName(path)));
            return;
        }

        _logger.LogInformation("Menu klik kanan: {Path} milik {Program}", path, program.DisplayName);
        SelectProgram(program);
        if (SelectedProgram is { } item && item.Model.Id == program.Id && _uninstall.Value.Start([item], AllModels()))
        {
            await RefreshAsync();
        }
    }

    /// <summary>Memuat ikon di latar belakang (2 utas) dan memasangnya di utas UI.</summary>
    private Task LoadIconsAsync(IEnumerable<ProgramItemViewModel> items, CancellationToken cancellationToken)
    {
        SynchronizationContext? ui = SynchronizationContext.Current;
        var list = items.ToList();
        var options = new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancellationToken };
        return Task.Run(() => Parallel.ForEach(list, options, item =>
        {
            if (_icons.Load(item.Model) is { } icon)
            {
                if (ui is null)
                {
                    item.Icon = icon;
                }
                else
                {
                    ui.Post(_ => item.Icon = icon, null);
                }
            }
        }), cancellationToken);
    }

    /// <summary>Memuat ikon lalu menghitung ukuran folder untuk program tanpa EstimatedSize.</summary>
    private async Task LoadDetailsAsync(IReadOnlyList<ProgramItemViewModel> items, CancellationToken cancellationToken)
    {
        SynchronizationContext? ui = SynchronizationContext.Current;
        void OnUi(Action action)
        {
            if (ui is null)
            {
                action();
            }
            else
            {
                ui.Post(_ => action(), null);
            }
        }

        try
        {
            // Ikon hanya untuk entri yang tampil: komponen sistem (±300, tersembunyi bawaan) dimuat saat opsinya
            // dinyalakan. 2 utas saja agar antarmuka tidak berebut CPU dengan pemuatan latar.
            var ordered = items.OrderBy(i => i.Model.IsSystemComponent).ToList();
            var iconTask = LoadIconsAsync(ordered.Where(i => !i.Model.IsSystemComponent || ShowSystemComponents), cancellationToken);

            // Versi 1.2: perkiraan terakhir dipakai (Prefetch/UserAssist) untuk label JARANG.
            var usageTask = Task.Run(() =>
            {
                UsageIndex index = _usage.Load();
                if (index.Count == 0)
                {
                    return;
                }

                DateOnly today = _clock.Today;
                foreach (var item in ordered.Where(i => !i.Model.IsSystemComponent && i.Model.Source != ProgramSource.Store))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string? location = InstallLocationInference.WithInferredLocation(item.Model, _locationRules).InstallLocation;
                    if (_locationRules.IsMeasurable(location)
                        && index.LastUsed(_usage.ExecutablesIn(location!)) is { } lastUsed)
                    {
                        var date = DateOnly.FromDateTime(lastUsed.LocalDateTime);
                        OnUi(() => item.ApplyLastUsed(date, today));
                    }
                }
            }, cancellationToken);

            var toMeasure = items
                .Where(i => !i.Model.IsSystemComponent
                            && i.Model.SizeBytes is null
                            && _locationRules.IsMeasurable(i.Model.InstallLocation))
                .ToList();

            if (toMeasure.Count > 0)
            {
                _notice = Strings.Format("Status_ComputingSizes", IndonesianFormat.Count(toMeasure.Count));
                UpdateStatus();

                var sizeOptions = new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancellationToken };
                await Task.Run(() => Parallel.ForEach(toMeasure, sizeOptions, item =>
                {
                    if (_folderSizes.Measure(item.Model.InstallLocation!, cancellationToken) is { } size)
                    {
                        OnUi(() => item.ApplyComputedSize(size));
                    }
                }), cancellationToken);
                _folderSizes.Save();
            }

            await iconTask;
            await usageTask;

            OnUi(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                _notice = null;
                if (SortKey == ProgramSortKey.Size)
                {
                    ProgramsView.Refresh();
                }

                UpdateStatus();
                _logger.LogInformation("Ikon dan ukuran selesai dimuat ({Measured} folder dihitung)", toMeasure.Count);
            });
        }
        catch (OperationCanceledException)
        {
            // Daftar dimuat ulang.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gagal memuat ikon/ukuran");
            OnUi(() =>
            {
                _notice = null;
                UpdateStatus();
            });
        }
    }

    // ---- Hapus instalan (Tahap 3) ----

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task UninstallAsync()
    {
        if (_uninstall.Value.Start([.. _selectedPrograms], AllModels()))
        {
            await RefreshAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ForceUninstallAsync()
    {
        if (_uninstall.Value.Start([.. _selectedPrograms], AllModels(), forceMode: true))
        {
            await RefreshAsync();
        }
    }

    /// <summary>Semua program yang sedang dimuat (termasuk komponen sistem), untuk melindungi milik program lain.</summary>
    private IReadOnlyList<InstalledProgram> AllModels() => [.. _programs.Select(p => p.Model)];

    private HunterWindow? _hunterWindow;

    [RelayCommand]
    private void HunterMode()
    {
        if (_hunterWindow is { IsLoaded: true })
        {
            _hunterWindow.Activate();
            return;
        }

        var main = System.Windows.Application.Current.MainWindow;
        _hunterWindow = new HunterWindow(
            _hunter,
            () => [.. _programs.Select(p => p.Model)],
            select: program =>
            {
                SelectProgram(program);
                main.WindowState = System.Windows.WindowState.Normal;
                main.Activate();
            },
            uninstall: async program =>
            {
                main.WindowState = System.Windows.WindowState.Normal;
                SelectProgram(program);
                if (SelectedProgram is { } item && _uninstall.Value.Start([item], AllModels()))
                {
                    await RefreshAsync();
                }
            });

        // Jendela utama diperkecil supaya program target terlihat; dipulihkan saat Mode Pemburu ditutup.
        _hunterWindow.Closed += (_, _) =>
        {
            _hunterWindow = null;
            if (main.WindowState == System.Windows.WindowState.Minimized)
            {
                main.WindowState = System.Windows.WindowState.Normal;
            }
        };
        _hunterWindow.Show();
        main.WindowState = System.Windows.WindowState.Minimized;
    }

    /// <summary>Berkas (.lnk/.exe) dijatuhkan ke jendela utama: kenali programnya lalu pilih.</summary>
    public void HuntFile(string path)
    {
        var result = _hunter.IdentifyFile(path, [.. _programs.Select(p => p.Model)]);
        if (result.Program is { } program)
        {
            SelectProgram(program);
            _notice = Strings.Format("Hunter_Found", program.DisplayName);
        }
        else
        {
            _notice = Strings.Get("Hunter_NotFound");
        }

        UpdateStatus();
    }

    /// <summary>Memilih program di daftar; pencarian dikosongkan dan komponen sistem dimunculkan bila perlu.</summary>
    public void SelectProgram(InstalledProgram program)
    {
        var item = _programs.FirstOrDefault(p => p.Model.Id == program.Id);
        if (item is null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(SearchText))
        {
            SearchText = string.Empty;
        }

        if (item.Model.IsSystemComponent && !ShowSystemComponents)
        {
            ShowSystemComponents = true;
        }

        SelectedProgram = item;
    }


    /// <summary>Jendela "Program awal" (versi 1.2).</summary>
    [RelayCommand]
    private void Startup()
    {
        StartupWindow? window = null;
        var viewModel = new StartupViewModel(_startup, _shell, AllModels, _icons, _logger,
            removeEntry: entry => ChoiceDialog.Show(window, Strings.Get("Startup_RemoveEntry"),
                    Strings.Format("Startup_ConfirmRemove", entry.Name, entry.ExecutablePath ?? entry.Command),
                [
                    new ChoiceButton(Strings.Get("Common_Cancel"), ChoiceKind.Primary, IsDefault: true, IsCancel: true),
                    new ChoiceButton(Strings.Get("Startup_RemoveEntry"), ChoiceKind.Danger),
                ],
                glyph: "") == 1
                && _startupCleanup.Value.RemoveMissing(entry));
        window = new StartupWindow(viewModel) { Owner = System.Windows.Application.Current.MainWindow };
        window.ShowDialog();
    }

    /// <summary>Jendela "Ekstensi browser" (versi 1.3).</summary>
    [RelayCommand]
    private void Extensions()
    {
        var owner = System.Windows.Application.Current.MainWindow;
        BrowserExtensionsWindow? window = null;
        var viewModel = new BrowserExtensionsViewModel(_extensions, _extensionService.Value, _shell,
            confirmFirefox: (name, profile) => ChoiceDialog.Show(window, Strings.Get("Extensions_Remove"),
                Strings.Format("Extensions_ConfirmFirefox", name, profile),
            [
                new ChoiceButton(Strings.Get("Common_Cancel"), ChoiceKind.Primary, IsDefault: true, IsCancel: true),
                new ChoiceButton(Strings.Get("Extensions_Remove"), ChoiceKind.Danger),
            ],
            glyph: "") == 1);
        window = new BrowserExtensionsWindow(viewModel) { Owner = owner };
        window.ShowDialog();
    }

    /// <summary>Jendela "Program portable" (versi 2.0).</summary>
    [RelayCommand]
    private void Portable()
    {
        var owner = System.Windows.Application.Current.MainWindow;
        PortableProgramsWindow? window = null;
        var viewModel = new PortableProgramsViewModel(_portableScanner, _portableService.Value, _shell, AllModels,
            confirm: program => ChoiceDialog.Show(window, Strings.Get("Portable_Remove"),
                Strings.Format("Portable_Confirm", program.Name, program.Folder,
                    program.SizeBytes is { } size ? IndonesianFormat.Size(size) : "?",
                    IndonesianFormat.Count(program.Shortcuts.Count))
                    + (program.IsInAppData ? Environment.NewLine + Environment.NewLine + Strings.Get("Portable_ConfirmAppData") : string.Empty),
            [
                new ChoiceButton(Strings.Get("Common_Cancel"), ChoiceKind.Primary, IsDefault: true, IsCancel: true),
                new ChoiceButton(Strings.Get("Portable_Remove"), ChoiceKind.Danger),
            ],
            glyph: "") == 1,
            pickFolder: () =>
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Strings.Get("Portable_ScanFolder") };
                return dialog.ShowDialog(window) == true ? dialog.FolderName : null;
            },
            _clock.Today,
            _logger);
        window = new PortableProgramsWindow(viewModel) { Owner = owner };
        window.ShowDialog();
    }

    /// <summary>Jendela "Pantau instalasi" (versi 2.0).</summary>
    [RelayCommand]
    private async Task InstallMonitorAsync()
    {
        var owner = System.Windows.Application.Current.MainWindow;
        InstallMonitorWindow? window = null;
        using var viewModel = new InstallMonitorViewModel(_snapshotter, _traces, _clock,
            pickInstaller: () =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = Strings.Get("Monitor_RunInstaller"),
                    Filter = Strings.Get("Monitor_InstallerFilter"),
                };
                return dialog.ShowDialog(window) == true ? dialog.FileName : null;
            },
            launch: LaunchInstaller,
            confirmDelete: name => ChoiceDialog.Show(window, Strings.Get("Monitor_DeleteTrace"),
                Strings.Format("Monitor_ConfirmDeleteTrace", name),
            [
                new ChoiceButton(Strings.Get("Common_Cancel"), ChoiceKind.Primary, IsDefault: true, IsCancel: true),
                new ChoiceButton(Strings.Get("Monitor_DeleteTrace"), ChoiceKind.Danger),
            ],
            glyph: "") == 1,
            _logger);
        window = new InstallMonitorWindow(viewModel) { Owner = owner };
        window.ShowDialog();

        // Program yang baru dipasang muncul di daftar.
        await RefreshAsync();
    }

    /// <summary>Installer pilihan pengguna dibuka seperti klik ganda di Explorer (.exe/.msi).</summary>
    private bool LaunchInstaller(string path)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            _logger.LogInformation("Installer dijalankan saat pemantauan: {Path}", path);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _logger.LogWarning(ex, "Installer {Path} tidak bisa dijalankan", path);
            return false;
        }
    }

    /// <summary>Jendela "Pembaruan program" lewat winget (versi 2.0).</summary>
    [RelayCommand]
    private async Task WingetAsync()
    {
        var owner = System.Windows.Application.Current.MainWindow;
        WingetWindow? window = null;
        using var viewModel = new WingetViewModel(_winget,
            confirm: (action, message) => ChoiceDialog.Show(window, action, message,
            [
                new ChoiceButton(Strings.Get("Common_Cancel"), ChoiceKind.Secondary, IsCancel: true),
                new ChoiceButton(action, ChoiceKind.Primary, IsDefault: true),
            ],
            glyph: "") == 1,
            _logger);
        window = new WingetWindow(viewModel) { Owner = owner };
        window.ShowDialog();
        if (viewModel.AnyChanged)
        {
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task HistoryAsync()
    {
        // Sesi yang dibatalkan bisa mengembalikan entri program: segarkan daftar.
        if (_history.Value.Show())
        {
            await RefreshAsync();
        }
    }


    /// <summary>Hanya untuk entri yang penghapus instalannya tidak ada/tidak bisa dijalankan.</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveBrokenEntry))]
    private async Task RemoveBrokenEntryAsync()
    {
        if (SelectedProgram is { } item && _uninstall.Value.RemoveBrokenEntry(item.Model))
        {
            await RefreshAsync();
        }
    }

    private bool CanRemoveBrokenEntry() =>
        SelectedProgram is { } item && item.Model.Source != ProgramSource.Store && _uninstall.Value.IsBroken(item.Model);

    // ---- Aksi yang sudah berfungsi ----

    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private void OpenFolder()
    {
        if (SelectedProgram is { HasLocation: true } program && !_shell.OpenFolder(program.Model.InstallLocation!))
        {
            _dialogs.ShowWarning(Strings.Get("AppName"), Strings.Format("Folder_NotFound", program.Model.InstallLocation));
        }
    }

    private bool CanOpenFolder() => SelectedProgram is { HasLocation: true };

    [RelayCommand(CanExecute = nameof(CanOpenWebsite))]
    private void OpenWebsite()
    {
        if (SelectedProgram?.Model.WebsiteUrl is { } url)
        {
            _shell.OpenUrl(url);
        }
    }

    private bool CanOpenWebsite() => SelectedProgram is { HasWebsite: true };

    [RelayCommand(CanExecute = nameof(HasSelectedProgram))]
    private void SearchWeb()
    {
        if (SelectedProgram is { } program)
        {
            _shell.OpenUrl("https://www.google.com/search?q=" + Uri.EscapeDataString(program.DisplayName));
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedProgram))]
    private void CopyName()
    {
        if (SelectedProgram is { } program)
        {
            _shell.CopyText(program.DisplayName);
            _notice = Strings.Get("Status_NameCopied");
            UpdateStatus();
        }
    }

    private bool HasSelectedProgram() => SelectedProgram is not null;

    [RelayCommand(CanExecute = nameof(CanOpenRegistry))]
    private void OpenRegistry()
    {
        if (SelectedProgram?.Model.RegistryKeyPath is { } path && !_shell.OpenRegistryEditor(path))
        {
            _dialogs.ShowWarning(Strings.Get("AppName"), Strings.Get("Registry_OpenFailed"));
        }
    }

    private bool CanOpenRegistry() => SelectedProgram?.Model.RegistryKeyPath is not null;

    [RelayCommand]
    private void SortBy(ProgramSortKey key)
    {
        if (SortKey == key)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortKey = key;
            // Ukuran dan tanggal lebih berguna dari yang terbesar/terbaru.
            SortDescending = key is ProgramSortKey.Size or ProgramSortKey.InstallDate;
        }
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    partial void OnSearchTextChanged(string value)
    {
        ProgramsView.Refresh();
        UpdateStatus();
    }

    partial void OnShowSystemComponentsChanged(bool value)
    {
        if (_initializing)
        {
            return;
        }

        if (value)
        {
            // Ikon komponen sistem belum dimuat saat aplikasi dibuka.
            _ = LoadIconsAsync([.. _programs.Where(p => p.Model.IsSystemComponent && p.Icon is null)], _loadCts?.Token ?? CancellationToken.None);
        }

        ProgramsView.Refresh();
        UpdateStatus();
        SaveSettings();
    }

    partial void OnViewModeChanged(ProgramViewMode value)
    {
        if (!_initializing)
        {
            SaveSettings();
        }
    }

    partial void OnSortKeyChanged(ProgramSortKey value)
    {
        if (_initializing)
        {
            return;
        }

        ApplySort();
        SaveSettings();
    }

    partial void OnSortDescendingChanged(bool value)
    {
        if (_initializing)
        {
            return;
        }

        ApplySort();
        SaveSettings();
    }

    private bool FilterProgram(object item) =>
        item is ProgramItemViewModel program
        && IsListed(program)
        && ProgramSearch.Matches(program.Model, SearchText);

    /// <summary>Lolos saringan komponen sistem (sebelum pencarian).</summary>
    private bool IsListed(ProgramItemViewModel program) => ShowSystemComponents || !program.Model.IsSystemComponent;

    private void ApplySort()
    {
        var comparer = new ProgramComparer(SortKey, SortDescending);
        ProgramsView.CustomSort = Comparer<object>.Create((a, b) =>
            comparer.Compare((a as ProgramItemViewModel)?.Model, (b as ProgramItemViewModel)?.Model));
    }

    private void UpdateStatus(bool evenWhileLoading = false)
    {
        if (IsLoading && !evenWhileLoading)
        {
            return;
        }

        var listed = _programs.Where(IsListed).ToList();
        int total = listed.Count;
        int shown = ProgramsView.Count;
        string selected = IndonesianFormat.Count(_selectedPrograms.Count);

        if (shown == total)
        {
            long size = listed.Sum(p => p.SizeBytes);
            StatusText = Strings.Format("Status_Summary", IndonesianFormat.Count(total), IndonesianFormat.Size(size), selected);
        }
        else
        {
            long size = ProgramsView.Cast<ProgramItemViewModel>().Sum(p => p.SizeBytes);
            StatusText = Strings.Format("Status_Filtered", IndonesianFormat.Count(shown), IndonesianFormat.Count(total), IndonesianFormat.Size(size), selected);
        }

        EmptyText = shown == 0 && total > 0 && !string.IsNullOrWhiteSpace(SearchText)
            ? Strings.Format("Status_NoResults", SearchText.Trim())
            : null;

        NoticeText = _notice;
    }

    private void SaveSettings()
    {
        _settings = _settingsStore.Load() with
        {
            ViewMode = ViewMode,
            SortKey = SortKey,
            SortDescending = SortDescending,
            ShowSystemComponents = ShowSystemComponents,
        };

        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Gagal menyimpan pengaturan tampilan");
        }
    }
}

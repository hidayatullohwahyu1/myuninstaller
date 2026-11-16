using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyUninstaller.App.Localization;
using MyUninstaller.App.Services;
using MyUninstaller.Core.Formatting;
using MyUninstaller.Core.Models;
using MyUninstaller.Core.Portable;

namespace MyUninstaller.App.ViewModels;

/// <summary>Satu baris program portable.</summary>
public sealed class PortableProgramItemViewModel(PortableProgram program, DateOnly today)
{
    public PortableProgram Program { get; } = program;

    public string Name => Program.Name;

    public string Folder => Program.Folder;

    public bool IsInAppData => Program.IsInAppData;

    public string DetailText => string.Join(" · ", new[]
    {
        Program.Publisher,
        Program.Version,
        Program.SizeBytes is { } size and > 0 ? IndonesianFormat.Size(size) : null,
        Program.LastUsed is { } used ? Strings.Format("Portable_LastUsed", IndonesianFormat.Date(DateOnly.FromDateTime(used.LocalDateTime))) : null,
        Program.Shortcuts.Count > 0 ? Strings.Format("Portable_Shortcuts", IndonesianFormat.Count(Program.Shortcuts.Count)) : null,
    }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>Dipakai dalam 90 hari terakhir tidak diketahui → tidak diberi label (sama dengan daftar utama).</summary>
    public bool IsRarelyUsed => Program.LastUsed is { } used
                                && today.DayNumber - DateOnly.FromDateTime(used.LocalDateTime).DayNumber > InstalledProgram.RarelyUsedThresholdDays;
}

/// <summary>Jendela "Program portable" (versi 2.0): pindai folder umum atau folder pilihan, hapus ke karantina.</summary>
public sealed partial class PortableProgramsViewModel : ObservableObject, IDisposable
{
    private readonly IPortableProgramScanner _scanner;
    private readonly PortableProgramService _service;
    private readonly IShellService _shell;
    private readonly Func<IReadOnlyList<InstalledProgram>> _installed;
    private readonly Func<PortableProgram, bool> _confirm;
    private readonly Func<string?> _pickFolder;
    private readonly DateOnly _today;
    private readonly ILogger _logger;
    private CancellationTokenSource? _scanCts;

    public PortableProgramsViewModel(
        IPortableProgramScanner scanner,
        PortableProgramService service,
        IShellService shell,
        Func<IReadOnlyList<InstalledProgram>> installed,
        Func<PortableProgram, bool> confirm,
        Func<string?> pickFolder,
        DateOnly today,
        ILogger logger)
    {
        _scanner = scanner;
        _service = service;
        _shell = shell;
        _installed = installed;
        _confirm = confirm;
        _pickFolder = pickFolder;
        _today = today;
        _logger = logger;
    }

    public ObservableCollection<PortableProgramItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand), nameof(OpenFolderCommand))]
    public partial PortableProgramItemViewModel? Selected { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(ScanFolderCommand), nameof(RemoveCommand), nameof(StopCommand))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    public partial string SummaryText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? NoticeText { get; set; }

    public bool IsEmpty => !IsScanning && Items.Count == 0;

    public bool AnyRemoved { get; private set; }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private Task ScanAsync() => RunScanAsync(_scanner.DefaultRoots);

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanFolderAsync()
    {
        if (_pickFolder() is { } folder)
        {
            await RunScanAsync([folder]);
        }
    }

    private bool CanScan() => !IsScanning;

    [RelayCommand(CanExecute = nameof(IsScanning))]
    private void Stop() => _scanCts?.Cancel();

    private async Task RunScanAsync(IReadOnlyList<string> roots)
    {
        var cts = _scanCts = new CancellationTokenSource();
        IsScanning = true;
        NoticeText = null;
        SummaryText = Strings.Get("Portable_Scanning");
        Items.Clear();

        try
        {
            var installed = _installed();
            var found = await Task.Run(() => _scanner.Scan(roots, installed, cts.Token), cts.Token);
            foreach (var program in found)
            {
                Items.Add(new PortableProgramItemViewModel(program, _today));
            }

            long total = found.Sum(p => p.SizeBytes ?? 0);
            SummaryText = Strings.Format("Portable_Summary", IndonesianFormat.Count(found.Count), IndonesianFormat.Size(total));
        }
        catch (OperationCanceledException)
        {
            SummaryText = Strings.Get("Portable_Stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pemindaian program portable gagal");
            SummaryText = Strings.Get("Portable_ScanFailed");
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove()
    {
        var item = Selected!;
        var result = _service.Remove(item.Program, _confirm);
        if (result.Message.Length > 0)
        {
            NoticeText = result.Message;
        }

        if (result.Removed)
        {
            AnyRemoved = true;
            Items.Remove(item);
            Selected = null;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    private bool CanRemove() => Selected is not null && !IsScanning;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenFolder()
    {
        if (Directory.Exists(Selected!.Folder))
        {
            _shell.OpenFolder(Selected.Folder);
        }
    }

    private bool HasSelection() => Selected is not null;

    /// <summary>Menghentikan pemindaian yang berjalan. Aman dipanggil lebih dari sekali.</summary>
    public void Dispose()
    {
        // CTS tidak di-Dispose: pemindaian di latar belakang mungkin masih memeriksa token-nya.
        _scanCts?.Cancel();
    }
}

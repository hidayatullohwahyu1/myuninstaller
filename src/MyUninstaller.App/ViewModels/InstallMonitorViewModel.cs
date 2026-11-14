using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyUninstaller.App.Localization;
using MyUninstaller.Core.Abstractions;
using MyUninstaller.Core.Formatting;
using MyUninstaller.Core.Leftovers;
using MyUninstaller.Core.Monitoring;

namespace MyUninstaller.App.ViewModels;

public enum MonitorPhase
{
    /// <summary>Belum memantau: daftar jejak tersimpan tampil.</summary>
    Ready,

    /// <summary>Mengambil gambaran sistem.</summary>
    Working,

    /// <summary>Gambaran awal sudah ada; pengguna memasang program.</summary>
    Installing,

    /// <summary>Perbedaan ditampilkan; pengguna memilih lalu menyimpan.</summary>
    Review,
}

/// <summary>Satu benda baru hasil pemantauan (bisa dibatalkan centangnya bila bukan buatan installer).</summary>
public sealed partial class TraceItemViewModel(TraceItem item) : ObservableObject
{
    public TraceItem Item { get; } = item;

    public string KindText => Strings.Get("Kind_" + Item.Kind);

    public string LocationText => Item.ValueName is null ? Item.Location : $"{Item.Location} → {Item.ValueName}";

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = true;
}

/// <summary>Satu jejak tersimpan.</summary>
public sealed class InstallTraceItemViewModel(InstallTrace trace)
{
    public InstallTrace Trace { get; } = trace;

    public string Name => Trace.Name;

    public string DetailText => Strings.Format("Monitor_TraceDetail",
        IndonesianFormat.DateTime(Trace.CreatedAt.LocalDateTime), IndonesianFormat.Count(Trace.Items.Count));
}

/// <summary>
/// Jendela "Pantau instalasi" (versi 2.0): gambaran sebelum → pengguna memasang → gambaran sesudah → simpan jejak.
/// Jejak dipakai otomatis saat program itu nanti dihapus instalannya (pemindai sisa).
/// </summary>
public sealed partial class InstallMonitorViewModel : ObservableObject, IDisposable
{
    private readonly ISystemSnapshotter _snapshotter;
    private readonly IInstallTraceStore _store;
    private readonly IClock _clock;
    private readonly Func<string?> _pickInstaller;
    private readonly Func<string, bool> _launch;
    private readonly Func<string, bool> _confirmDelete;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _closing = new();
    private SystemSnapshot? _before;

    public InstallMonitorViewModel(
        ISystemSnapshotter snapshotter,
        IInstallTraceStore store,
        IClock clock,
        Func<string?> pickInstaller,
        Func<string, bool> launch,
        Func<string, bool> confirmDelete,
        ILogger logger)
    {
        _snapshotter = snapshotter;
        _store = store;
        _clock = clock;
        _pickInstaller = pickInstaller;
        _launch = launch;
        _confirmDelete = confirmDelete;
        _logger = logger;
        LoadTraces();
    }

    public ObservableCollection<TraceItemViewModel> Items { get; } = [];

    public ObservableCollection<InstallTraceItemViewModel> Traces { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(RunInstallerCommand), nameof(FinishCommand), nameof(SaveCommand),
        nameof(CancelCommand), nameof(DeleteTraceCommand))]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(IsWorking), nameof(IsInstalling), nameof(IsReview), nameof(IsActive), nameof(StepText))]
    public partial MonitorPhase Phase { get; set; } = MonitorPhase.Ready;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteTraceCommand))]
    public partial InstallTraceItemViewModel? SelectedTrace { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string ProgramName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? NoticeText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepText))]
    public partial string WorkingText { get; set; } = string.Empty;

    public bool IsReady => Phase == MonitorPhase.Ready;

    public bool IsWorking => Phase == MonitorPhase.Working;

    public bool IsInstalling => Phase == MonitorPhase.Installing;

    public bool IsReview => Phase == MonitorPhase.Review;

    /// <summary>Pemantauan sedang berjalan (bisa dibatalkan).</summary>
    public bool IsActive => IsInstalling || IsReview;

    public string StepText => Phase switch
    {
        MonitorPhase.Installing => Strings.Get("Monitor_StepInstall"),
        MonitorPhase.Review => Strings.Format("Monitor_StepReview", IndonesianFormat.Count(Items.Count)),
        MonitorPhase.Working => WorkingText,
        _ => Strings.Get("Monitor_StepStart"),
    };

    public bool HasTraces => Traces.Count > 0;

    private void LoadTraces()
    {
        Traces.Clear();
        foreach (var trace in _store.GetAll())
        {
            Traces.Add(new InstallTraceItemViewModel(trace));
        }

        OnPropertyChanged(nameof(HasTraces));
    }

    [RelayCommand(CanExecute = nameof(IsReady))]
    private async Task StartAsync()
    {
        NoticeText = null;
        var snapshot = await TakeAsync(Strings.Get("Monitor_TakingBefore"));
        if (snapshot is null)
        {
            Phase = MonitorPhase.Ready;
            return;
        }

        _before = snapshot;
        Phase = MonitorPhase.Installing;
    }

    [RelayCommand(CanExecute = nameof(IsInstalling))]
    private void RunInstaller()
    {
        if (_pickInstaller() is { } installer && !_launch(installer))
        {
            NoticeText = Strings.Get("Monitor_LaunchFailed");
        }
    }

    [RelayCommand(CanExecute = nameof(IsInstalling))]
    private async Task FinishAsync()
    {
        var after = await TakeAsync(Strings.Get("Monitor_TakingAfter"));
        if (after is null || _before is null)
        {
            Phase = MonitorPhase.Installing;
            return;
        }

        var added = InstallTraceRules.Added(_before, after);
        Items.Clear();
        foreach (var item in added)
        {
            Items.Add(new TraceItemViewModel(item));
        }

        // Nama program dari entri Uninstall baru (bila installer membuatnya).
        ProgramName = InstallTraceRules.UninstallKeysIn(added).Select(_snapshotter.ReadDisplayName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                      ?? string.Empty;
        _logger.LogInformation("Pantau instalasi: {Count} benda baru, program {Name}", added.Count, ProgramName);
        Phase = MonitorPhase.Review;
        if (added.Count == 0)
        {
            NoticeText = Strings.Get("Monitor_NothingNew");
        }
    }

    private async Task<SystemSnapshot?> TakeAsync(string text)
    {
        WorkingText = text;
        Phase = MonitorPhase.Working;
        try
        {
            return await Task.Run(() => _snapshotter.Take(_closing.Token), _closing.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gambaran sistem gagal");
            NoticeText = Strings.Get("Monitor_SnapshotFailed");
            return null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        var chosen = Items.Where(i => i.IsChecked).Select(i => i.Item).ToList();
        var trace = new InstallTrace
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = ProgramName.Trim(),
            CreatedAt = _clock.Now,
            Items = chosen,
            UninstallKeys = InstallTraceRules.UninstallKeysIn(chosen),
        };

        try
        {
            _store.Save(trace);
            NoticeText = Strings.Format("Monitor_Saved", trace.Name, IndonesianFormat.Count(chosen.Count));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Jejak tidak bisa disimpan");
            NoticeText = Strings.Get("Monitor_SaveFailed");
            return;
        }

        Reset();
        LoadTraces();
    }

    private bool CanSave() => IsReview && ProgramName.Trim().Length > 0 && Items.Any(i => i.IsChecked);

    /// <summary>Dipanggil saat centang berubah (dari tampilan).</summary>
    public void RefreshCanSave() => SaveCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        NoticeText = null;
        Reset();
    }

    private bool CanCancel() => IsActive;

    private void Reset()
    {
        _before = null;
        Items.Clear();
        ProgramName = string.Empty;
        Phase = MonitorPhase.Ready;
    }

    [RelayCommand(CanExecute = nameof(CanDeleteTrace))]
    private void DeleteTrace()
    {
        var selected = SelectedTrace!;
        if (!_confirmDelete(selected.Name))
        {
            return;
        }

        try
        {
            _store.Delete(selected.Trace.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Jejak tidak bisa dihapus");
        }

        LoadTraces();
    }

    private bool CanDeleteTrace() => IsReady && SelectedTrace is not null;

    public void Dispose() => _closing.Cancel();
}

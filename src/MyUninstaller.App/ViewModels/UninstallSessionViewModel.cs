using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyUninstaller.App.Localization;
using MyUninstaller.Core.Formatting;
using MyUninstaller.Core.Leftovers;
using MyUninstaller.Core.Models;
using MyUninstaller.Core.Safety;
using MyUninstaller.Core.Uninstall;

namespace MyUninstaller.App.ViewModels;

/// <summary>Pilihan pengguna saat program yang akan dihapus masih berjalan.</summary>
public enum RunningProcessChoice
{
    Kill,
    Ignore,
    Skip,
}

/// <summary>Status satu langkah di jendela proses.</summary>
public enum StepState
{
    Pending,
    Active,
    Done,
    Skipped,
}

public sealed partial class SessionStepViewModel(string label) : ObservableObject
{
    public string Label { get; } = label;

    [ObservableProperty]
    public partial StepState State { get; set; }
}

public sealed partial class SessionItemViewModel(ProgramItemViewModel program) : ObservableObject
{
    public ProgramItemViewModel Program { get; } = program;

    public string Name => Program.DisplayName;

    public ImageSource? Icon => Program.Icon;

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    /// <summary>Null selama belum selesai.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSuccess), nameof(IsWarning), nameof(IsError))]
    public partial UninstallOutcome? Outcome { get; set; }

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    public bool IsSuccess => Outcome == UninstallOutcome.Success;

    public bool IsWarning => Outcome is UninstallOutcome.Partial or UninstallOutcome.Cancelled;

    public bool IsError => Outcome is UninstallOutcome.Failed or UninstallOutcome.NoUninstaller;
}

/// <summary>Layanan yang dipakai satu sesi hapus instalan.</summary>
/// <param name="Runner">Pelaksana uninstaller bawaan.</param>
/// <param name="Processes">Daftar/penutupan proses.</param>
/// <param name="Journal">Jurnal sesi (dicatat sebelum setiap aksi).</param>
/// <param name="Remover">Penghapus aman (karantina + cadangan .reg) untuk Hapus paksa.</param>
/// <param name="PlanForce">Target Hapus paksa untuk satu program.</param>
/// <param name="CreateRestorePoint">Pembuat titik pemulihan; null bila dimatikan di Pengaturan.</param>
/// <param name="Leftovers">Pemindai sisa; null untuk melewati langkah sisa.</param>
/// <param name="AllPrograms">Semua program saat sesi dimulai (yang tidak ikut dihapus dianggap masih terpasang).</param>
public sealed record UninstallSessionServices(
    UninstallRunner Runner,
    IProcessInspector Processes,
    ISessionJournal Journal,
    SafeRemover Remover,
    Func<InstalledProgram, IReadOnlyList<RemovalTarget>> PlanForce,
    Func<string, CancellationToken, Task<RestorePointStatus>>? CreateRestorePoint,
    ILeftoverScanner? Leftovers = null,
    IReadOnlyList<InstalledProgram>? AllPrograms = null);

/// <summary>
/// Satu sesi hapus instalan (satu atau beberapa program, berurutan). Kegagalan satu program tidak
/// menghentikan antrean (PRD).
/// </summary>
public sealed partial class UninstallSessionViewModel : ObservableObject
{
    private readonly UninstallSessionServices _services;
    private readonly Func<string, IReadOnlyList<RunningProcess>, RunningProcessChoice> _askRunning;
    private readonly Func<bool> _confirmStop;
    private readonly ILogger _logger;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _stepClock = new();
    private CancellationTokenSource? _itemCts;
    private bool _restartRequired;
    private bool _pendingReboot;
    private string? _restorePointNote;
    private string _sessionId = string.Empty;

    public UninstallSessionViewModel(
        IReadOnlyList<ProgramItemViewModel> programs,
        UninstallMode mode,
        UninstallSessionServices services,
        Func<string, IReadOnlyList<RunningProcess>, RunningProcessChoice> askRunning,
        Func<bool> confirmStop,
        ILogger logger)
    {
        Mode = mode;
        _services = services;
        _askRunning = askRunning;
        _confirmStop = confirmStop;
        _logger = logger;

        Items = [.. programs.Select(p => new SessionItemViewModel(p))];
        Steps =
        [
            new(Strings.Get("Progress_StepCheck")),
            new(Strings.Get(mode == UninstallMode.Force ? "Progress_StepQuarantine" : "Progress_StepRun")),
            new(Strings.Get("Progress_StepVerify")),
            new(Strings.Get("Progress_StepLeftovers")),
        ];

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateElapsed();
    }

    public UninstallMode Mode { get; }

    /// <summary>Jalankan uninstaller dalam mode senyap bila didukung (uninstall massal, versi 1.1).</summary>
    public bool Silent { get; init; }

    public ObservableCollection<SessionItemViewModel> Items { get; }

    public ObservableCollection<SessionStepViewModel> Steps { get; }

    public bool IsMultiple => Items.Count > 1;

    [ObservableProperty]
    public partial SessionItemViewModel? Current { get; set; }

    [ObservableProperty]
    public partial string CounterText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ElapsedText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowWaitingHint { get; set; }

    /// <summary>Teks langkah tingkat sesi (titik pemulihan) sebelum program pertama diproses.</summary>
    [ObservableProperty]
    public partial string? SessionStepText { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial bool IsFinished { get; set; }

    [ObservableProperty]
    public partial string SummaryText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? NoteText { get; set; }

    /// <summary>Program yang berhasil/sebagian dihapus (untuk menyegarkan daftar).</summary>
    public bool AnyRemoved => Items.Any(i => i.Outcome is UninstallOutcome.Success or UninstallOutcome.Partial);

    /// <summary>Temuan sisa untuk layar hasil (mode Normal, Super, Hapus paksa). Mode Aman membersihkan langsung.</summary>
    public List<LeftoverItem> Leftovers { get; } = [];

    [ObservableProperty]
    public partial bool HasLeftoversToReview { get; set; }

    /// <summary>Selesai tanpa sisa untuk ditinjau.</summary>
    [ObservableProperty]
    public partial bool ShowPlainClose { get; set; }

    [ObservableProperty]
    public partial string? ReviewText { get; set; }

    /// <summary>True bila pengguna memilih "Tinjau sisa instalan" saat menutup.</summary>
    public bool ReviewRequested { get; private set; }

    public event EventHandler? RequestClose;

    /// <summary>ID sesi di jurnal/riwayat.</summary>
    public string SessionId => _sessionId;

    public async Task RunAsync()
    {
        IsRunning = true;
        var status = JournalSessionStatus.Completed;
        try
        {
            string title = Items.Count == 1 ? Items[0].Name : Strings.Format("Mode_HeaderMany", Items.Count);
            _sessionId = _services.Journal.StartSession(Mode.ToString(), title);

            await CreateRestorePointAsync(title);

            for (int i = 0; i < Items.Count; i++)
            {
                var item = Items[i];
                Current = item;
                item.IsCurrent = true;
                CounterText = Strings.Format("Progress_Counter", i + 1, Items.Count);

                try
                {
                    await RunItemAsync(item);
                }
                catch (Exception ex)
                {
                    // Satu kegagalan tidak menghentikan antrean.
                    _logger.LogError(ex, "Galat saat menghapus {Program}", item.Name);
                    item.Outcome = UninstallOutcome.Failed;
                    item.StatusText = Strings.Format("Outcome_Error", ex.Message);
                }
                finally
                {
                    item.IsCurrent = false;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sesi hapus instalan gagal");
            status = JournalSessionStatus.Interrupted;
        }
        finally
        {
            _timer.Stop();
            IsRunning = false;
            ShowWaitingHint = false;
            SessionStepText = null;
            if (_sessionId.Length > 0)
            {
                TryJournal(() => _services.Journal.FinishSession(_sessionId, status));
            }

            Finish();
        }
    }

    /// <summary>
    /// Titik pemulihan sekali per sesi, sebelum program pertama. Bila ditolak/nonaktif, sesi tetap lanjut
    /// dengan cadangan .reg dan karantina (PRD), dan pengguna diberi tahu di ringkasan.
    /// </summary>
    private async Task CreateRestorePointAsync(string title)
    {
        if (_services.CreateRestorePoint is not { } create)
        {
            return;
        }

        SessionStepText = Strings.Get("Progress_StepRestorePoint");
        string description = $"My Uninstaller! - {title}";
        long action = _services.Journal.LogPending(_sessionId, JournalActionKind.RestorePoint, description);

        RestorePointStatus result;
        try
        {
            result = await create(description, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Titik pemulihan gagal");
            result = RestorePointStatus.Failed;
        }

        _services.Journal.Complete(action, result == RestorePointStatus.Created ? JournalActionStatus.Done : JournalActionStatus.Skipped,
            Strings.Get("RestorePointResult_" + result));
        _restorePointNote = result switch
        {
            RestorePointStatus.Created => null,
            RestorePointStatus.Throttled => Strings.Get("RestorePoint_Throttled"),
            RestorePointStatus.Disabled => Strings.Get("RestorePoint_Disabled"),
            _ => Strings.Get("RestorePoint_Failed"),
        };
        SessionStepText = null;
    }

    private void TryJournal(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Gagal menulis jurnal sesi");
        }
    }

    private async Task RunItemAsync(SessionItemViewModel item)
    {
        foreach (var step in Steps)
        {
            step.State = StepState.Pending;
        }

        // 1. Pemeriksaan awal: program sedang berjalan?
        SetStep(0);
        item.StatusText = Strings.Get("Progress_StepCheck");
        var running = await Task.Run(() => RunningProcessRules.FindProgramProcesses(
            _services.Processes.GetRunningProcesses(), item.Program.Model.InstallLocation, Environment.ProcessId));

        if (running.Count > 0)
        {
            switch (_askRunning(item.Name, running))
            {
                case RunningProcessChoice.Skip:
                    item.Outcome = UninstallOutcome.Cancelled;
                    item.StatusText = Strings.Get("Outcome_Skipped");
                    return;
                case RunningProcessChoice.Kill:
                    bool allClosed = await Task.Run(() => running.Select(KillJournaled).ToList().All(ok => ok));
                    if (!allClosed)
                    {
                        item.StatusText = Strings.Get("Running_KillFailed");
                    }

                    break;
            }
        }

        if (Mode == UninstallMode.Force && item.Program.Model.Source != Core.Models.ProgramSource.Store)
        {
            await RunForceAsync(item);
            return;
        }

        // 2–3. Jalankan penghapus instalan lalu verifikasi. Dicatat di jurnal SEBELUM dijalankan.
        long action = _services.Journal.LogPending(_sessionId, JournalActionKind.RunUninstaller,
            item.Program.Model.UninstallString ?? item.Program.Model.Id);
        SetStep(1);
        item.StatusText = Strings.Get("Progress_StepRun");
        _stepClock.Restart();
        UpdateElapsed();
        _timer.Start();

        using var cts = new CancellationTokenSource();
        _itemCts = cts;
        UninstallResult result;
        try
        {
            result = await _services.Runner.RunAsync(item.Program.Model, silent: Silent, cts.Token);
        }
        catch
        {
            TryJournal(() => _services.Journal.Complete(action, JournalActionStatus.Failed));
            throw;
        }
        finally
        {
            _itemCts = null;
            _timer.Stop();
            ShowWaitingHint = false;
        }

        SetStep(2);
        Steps[2].State = StepState.Done;

        item.Outcome = result.Outcome;
        item.StatusText = DescribeOutcome(result);
        _restartRequired |= result.RestartRequired;
        TryJournal(() => _services.Journal.Complete(action,
            result.Outcome is UninstallOutcome.Success or UninstallOutcome.Partial ? JournalActionStatus.Done : JournalActionStatus.Failed,
            result.ExitCode is { } exitCode
                ? Strings.Format("Journal_OutcomeWithCode", Strings.Get("OutcomeShort_" + result.Outcome), exitCode)
                : Strings.Get("OutcomeShort_" + result.Outcome)));

        _logger.LogInformation("Hapus instalan {Program}: {Outcome} (kode {ExitCode}, {Seconds} detik)",
            item.Name, result.Outcome, result.ExitCode, (int)result.Elapsed.TotalSeconds);

        // 4. Sisa instalan: hanya bila program benar-benar sudah tidak terdaftar.
        await ScanLeftoversAsync(item);
    }

    /// <summary>
    /// Langkah "Pindai sisa". Mode Aman: sisa berlabel Aman langsung dipindah ke karantina.
    /// Mode Normal/Super/Hapus paksa: temuan dikumpulkan untuk layar hasil.
    /// </summary>
    private async Task ScanLeftoversAsync(SessionItemViewModel item)
    {
        if (!Mode.ScansLeftovers() || _services.Leftovers is not { } scanner
            || item.Outcome is not (UninstallOutcome.Success or UninstallOutcome.Partial))
        {
            Steps[3].State = StepState.Skipped;
            return;
        }

        SetStep(3);
        string outcomeText = item.StatusText;
        item.StatusText = Strings.Get("Progress_StepLeftovers");

        // Program lain di sesi ini juga sedang dihapus, jadi tidak dianggap "masih terpasang".
        var sessionIds = Items.Select(i => i.Program.Model.Id).ToHashSet();
        var stillInstalled = (_services.AllPrograms ?? []).Where(p => !sessionIds.Contains(p.Id)).ToList();

        IReadOnlyList<LeftoverItem> found;
        try
        {
            found = await scanner.ScanAsync(item.Program.Model, stillInstalled, deep: Mode == UninstallMode.Super);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Pindai sisa {Program} gagal", item.Name);
            found = [];
        }

        string leftoverText;
        if (Mode == UninstallMode.Safe)
        {
            var safe = found.Where(f => f.Group == LeftoverGroup.Safe).Select(f => f.ToRemovalTarget()).ToList();
            var results = safe.Count == 0 ? [] : await Task.Run(() => _services.Remover.Remove(_sessionId, safe));
            int cleaned = results.Count(r => r.Status is RemovalStatus.Removed or RemovalStatus.PendingReboot);
            _pendingReboot |= results.Any(r => r.Status == RemovalStatus.PendingReboot);
            leftoverText = cleaned > 0
                ? Strings.Format("Progress_LeftoversAutoCleaned", IndonesianFormat.Count(cleaned))
                : Strings.Get("Progress_LeftoversNone");
        }
        else
        {
            Leftovers.AddRange(found);
            leftoverText = found.Count > 0
                ? Strings.Format("Progress_LeftoversFound", IndonesianFormat.Count(found.Count))
                : Strings.Get("Progress_LeftoversNone");
        }

        Steps[3].State = StepState.Done;
        item.StatusText = $"{outcomeText} · {leftoverText}";
    }

    private bool KillJournaled(RunningProcess process)
    {
        long action = _services.Journal.LogPending(_sessionId, JournalActionKind.KillProcess, $"{process.Name} ({process.ProcessId}) {process.ExecutablePath}");
        bool ok = _services.Processes.TryKill(process);
        TryJournal(() => _services.Journal.Complete(action, ok ? JournalActionStatus.Done : JournalActionStatus.Failed));
        return ok;
    }

    /// <summary>Hapus paksa: folder instalasi dan kunci Uninstall lewat karantina + cadangan .reg.</summary>
    private async Task RunForceAsync(SessionItemViewModel item)
    {
        SetStep(1);
        item.StatusText = Strings.Get("Progress_StepQuarantine");
        _stepClock.Restart();
        UpdateElapsed();
        _timer.Start();

        IReadOnlyList<RemovalResult> results;
        try
        {
            var targets = _services.PlanForce(item.Program.Model);
            results = await Task.Run(() => _services.Remover.Remove(_sessionId, targets));
        }
        finally
        {
            _timer.Stop();
        }

        SetStep(2);
        Steps[2].State = StepState.Done;

        int paths = results.Count(r => r.Target.Kind == RemovalTargetKind.Path && r.Status is RemovalStatus.Removed or RemovalStatus.PendingReboot);
        int keys = results.Count(r => r.Target.Kind == RemovalTargetKind.RegistryKey && r.Status == RemovalStatus.Removed);
        int failed = results.Count(r => r.Status is RemovalStatus.Failed or RemovalStatus.Protected);
        _pendingReboot |= results.Any(r => r.Status == RemovalStatus.PendingReboot);

        item.Outcome = results.Count == 0 || paths + keys == 0
            ? UninstallOutcome.Failed
            : failed > 0 ? UninstallOutcome.Partial : UninstallOutcome.Success;
        item.StatusText = results.Count == 0
            ? Strings.Get("Force_NoTargets")
            : Strings.Format(failed > 0 ? "Force_ResultWithFailures" : "Force_Result", paths, keys, failed);

        _logger.LogInformation("Hapus paksa {Program}: {Paths} folder/berkas, {Keys} kunci, {Failed} gagal", item.Name, paths, keys, failed);

        // Setelah folder dan entri utama masuk karantina, cari sisa lainnya (AppData, pintasan, Run, ...).
        await ScanLeftoversAsync(item);
    }

    private static string DescribeOutcome(UninstallResult result)
    {
        string text = Strings.Get(result.Outcome switch
        {
            UninstallOutcome.Success => "Outcome_Success",
            UninstallOutcome.Partial => "Outcome_Partial",
            UninstallOutcome.Failed => "Outcome_Failed",
            UninstallOutcome.Cancelled => "Outcome_Cancelled",
            _ => "Outcome_NoUninstaller",
        });

        if (result.Outcome is UninstallOutcome.Failed && result.ExitCode is { } code)
        {
            text += $" ({Strings.Format("Outcome_ExitCode", code)})";
        }

        return text;
    }

    private void SetStep(int index)
    {
        for (int i = 0; i < Steps.Count; i++)
        {
            if (i < index && Steps[i].State != StepState.Skipped)
            {
                Steps[i].State = StepState.Done;
            }
            else if (i == index)
            {
                Steps[i].State = StepState.Active;
            }
        }
    }

    private void UpdateElapsed()
    {
        TimeSpan elapsed = _stepClock.Elapsed;
        string time = elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\.mm\.ss") : elapsed.ToString(@"m\.ss");
        ElapsedText = Strings.Format("Progress_Elapsed", time);
        ShowWaitingHint = elapsed >= UninstallRunner.WaitingForInputHint;
    }

    private void Finish()
    {
        Current = null;
        int success = Items.Count(i => i.Outcome == UninstallOutcome.Success);
        int partial = Items.Count(i => i.Outcome == UninstallOutcome.Partial);
        int failed = Items.Count(i => i.Outcome is UninstallOutcome.Failed or UninstallOutcome.NoUninstaller);
        SummaryText = Strings.Format("Progress_Summary", IndonesianFormat.Count(success), IndonesianFormat.Count(partial), IndonesianFormat.Count(failed));

        var notes = new List<string>();
        if (_restorePointNote is not null)
        {
            notes.Add(_restorePointNote);
        }

        if (_pendingReboot)
        {
            notes.Add(Strings.Get("Progress_PendingReboot"));
        }

        if (_restartRequired)
        {
            notes.Add(Strings.Get("Progress_RestartNeeded"));
        }

        NoteText = notes.Count > 0 ? string.Join(Environment.NewLine, notes) : null;
        HasLeftoversToReview = Leftovers.Count > 0;
        ShowPlainClose = !HasLeftoversToReview;
        ReviewText = HasLeftoversToReview ? Strings.Format("Progress_ReviewLeftovers", IndonesianFormat.Count(Leftovers.Count)) : null;
        IsFinished = true;
    }

    /// <summary>Tutup jendela proses lalu buka layar hasil sisa.</summary>
    [RelayCommand]
    private void Review()
    {
        if (!IsRunning && HasLeftoversToReview)
        {
            ReviewRequested = true;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop()
    {
        if (_itemCts is { IsCancellationRequested: false } cts && _confirmStop())
        {
            cts.Cancel();
        }
    }

    [RelayCommand]
    private void Close()
    {
        if (!IsRunning)
        {
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
    }
}

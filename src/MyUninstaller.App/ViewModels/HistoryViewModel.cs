using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyUninstaller.App.Localization;
using MyUninstaller.App.Services;
using MyUninstaller.Core.Abstractions;
using MyUninstaller.Core.Formatting;
using MyUninstaller.Core.Safety;

namespace MyUninstaller.App.ViewModels;

/// <summary>Satu sesi di daftar Riwayat.</summary>
public sealed partial class HistorySessionViewModel(JournalSession session) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsRestored), nameof(IsProblem))]
    public partial JournalSession Session { get; set; } = session;

    public string Title => Session.Title;

    public string DateText => IndonesianFormat.DateTime(Session.StartedAt.LocalDateTime);

    public string ModeText => Text("SessionMode_", Session.Mode);

    public string StatusText => Text("SessionStatus_", Session.Status.ToString());

    public bool IsRestored => Session.Status == JournalSessionStatus.Restored;

    public bool IsProblem => Session.Status is JournalSessionStatus.Interrupted or JournalSessionStatus.Running;

    internal static string Text(string prefix, string name)
    {
        string text = Strings.Get(prefix + name);
        return text.StartsWith("[[", StringComparison.Ordinal) ? name : text; // mode lama/tak dikenal tampil apa adanya
    }
}

/// <summary>Satu aksi di rincian sesi.</summary>
public sealed class HistoryActionViewModel(JournalAction action, bool restorable)
{
    public JournalAction Action { get; } = action;

    public string KindText => HistorySessionViewModel.Text("ActionKind_", Action.Kind.ToString());

    public string StatusText => HistorySessionViewModel.Text("ActionStatus_", Action.Status.ToString());

    public string Target => Action.Target;

    public string? Detail => Action.Detail;

    public string? BackupPath => Action.BackupPath;

    public bool IsFailed => Action.Status == JournalActionStatus.Failed;

    public bool IsRestored => Action.Status == JournalActionStatus.Restored;

    public bool IsRestorable { get; } = restorable;

    public string TimeText => IndonesianFormat.Time(TimeOnly.FromDateTime(Action.CreatedAt.LocalDateTime));
}

/// <summary>
/// Menu Riwayat (PRD): daftar sesi, rincian aksi, Batalkan per sesi (berkas dari karantina + impor ulang .reg),
/// Salin laporan, buka/kosongkan karantina sesi.
/// </summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly ISessionJournal _journal;
    private readonly SessionRestorer _restorer;
    private readonly IQuarantineService _quarantine;
    private readonly IFolderSizeCalculator _sizes;
    private readonly IShellService _shell;
    private readonly ISettingsStore _settings;
    private readonly HistoryDialogs _dialogs;
    private readonly string _logFolder;
    private readonly ILogger _logger;

    public HistoryViewModel(
        ISessionJournal journal,
        SessionRestorer restorer,
        IQuarantineService quarantine,
        IFolderSizeCalculator sizes,
        IShellService shell,
        ISettingsStore settings,
        HistoryDialogs dialogs,
        string logFolder,
        ILogger logger)
    {
        _journal = journal;
        _restorer = restorer;
        _quarantine = quarantine;
        _sizes = sizes;
        _shell = shell;
        _settings = settings;
        _dialogs = dialogs;
        _logFolder = logFolder;
        _logger = logger;
        Load();
    }

    public ObservableCollection<HistorySessionViewModel> Sessions { get; } = [];

    public ObservableCollection<HistoryActionViewModel> Actions { get; } = [];

    public bool IsEmpty => Sessions.Count == 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand), nameof(CopyReportCommand), nameof(OpenQuarantineCommand), nameof(PurgeCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial HistorySessionViewModel? Selected { get; set; }

    public bool HasSelection => Selected is not null;

    [ObservableProperty]
    public partial string QuarantineText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? SummaryText { get; set; }

    [ObservableProperty]
    public partial string? RestorableText { get; set; }

    [ObservableProperty]
    public partial string? NoticeText { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand), nameof(PurgeCommand))]
    public partial bool IsWorking { get; set; }

    /// <summary>True bila ada sesi yang dibatalkan (daftar program perlu disegarkan).</summary>
    public bool AnyRestored { get; private set; }

    private SessionSummary? _summary;
    private bool _quarantineExists;

    private void Load()
    {
        string? keep = Selected?.Session.Id;
        Sessions.Clear();
        foreach (var session in _journal.GetSessions())
        {
            Sessions.Add(new HistorySessionViewModel(session));
        }

        OnPropertyChanged(nameof(IsEmpty));
        Selected = Sessions.FirstOrDefault(s => s.Session.Id == keep) ?? Sessions.FirstOrDefault();
        UpdateQuarantineText();
    }

    private void UpdateQuarantineText()
    {
        string root = Path.GetDirectoryName(_quarantine.SessionFolder("x"))!;
        long size = Directory.Exists(root) ? _sizes.Measure(root) ?? 0 : 0;
        QuarantineText = Strings.Format("History_Quarantine", IndonesianFormat.Size(size), _settings.Load().QuarantineRetentionDays);
    }

    partial void OnSelectedChanged(HistorySessionViewModel? value)
    {
        Actions.Clear();
        _summary = null;
        SummaryText = null;
        RestorableText = null;
        if (value is null)
        {
            return;
        }

        var actions = _journal.GetActions(value.Session.Id);
        foreach (var action in actions)
        {
            Actions.Add(new HistoryActionViewModel(action, HistoryReport.IsRestorable(action, BackupExists)));
        }

        _summary = HistoryReport.Summarize(actions, BackupExists);
        SummaryText = Strings.Format("History_Summary", _summary.Paths, _summary.RegistryItems, _summary.ServicesAndTasks);
        RestorableText = _summary.Restorable > 0
            ? Strings.Format("History_Restorable", _summary.Restorable)
            : Strings.Get("History_NotRestorable");
        _quarantineExists = Directory.Exists(_quarantine.SessionFolder(value.Session.Id));

        UndoCommand.NotifyCanExecuteChanged();
        OpenQuarantineCommand.NotifyCanExecuteChanged();
        PurgeCommand.NotifyCanExecuteChanged();
    }

    private static bool BackupExists(string path) => File.Exists(path) || Directory.Exists(path);

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        var session = Selected!;
        if (!_dialogs.ConfirmUndo(session.Title))
        {
            return;
        }

        IsWorking = true;
        try
        {
            var result = await Task.Run(() => _restorer.Restore(session.Session.Id));
            AnyRestored |= result.FilesRestored + result.KeysRestored > 0;
            NoticeText = result.Failed == 0
                ? Strings.Format("History_Undone", result.FilesRestored, result.KeysRestored)
                : Strings.Format("History_UndoneWithFailures", result.FilesRestored, result.KeysRestored, result.Failed);
            _logger.LogInformation("Sesi {Session} dibatalkan dari Riwayat: {Result}", session.Session.Id, result);
        }
        finally
        {
            IsWorking = false;
            Load();
        }
    }

    private bool CanUndo() => !IsWorking && Selected is { } s && _summary is { } sum && HistoryReport.CanUndo(s.Session, sum);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyReport()
    {
        var session = Selected!.Session;
        string version = typeof(HistoryViewModel).Assembly.GetName().Version?.ToString(3) ?? "?";
        string text = HistoryReport.BuildText(
            session,
            _journal.GetActions(session.Id),
            version,
            _logFolder,
            s => HistorySessionViewModel.Text("SessionStatus_", s.ToString()),
            k => HistorySessionViewModel.Text("ActionKind_", k.ToString()),
            a => HistorySessionViewModel.Text("ActionStatus_", a.ToString()));
        _shell.CopyText(text);
        NoticeText = Strings.Get("History_ReportCopied");
    }

    [RelayCommand(CanExecute = nameof(HasQuarantine))]
    private void OpenQuarantine() => _shell.OpenFolder(_quarantine.SessionFolder(Selected!.Session.Id));

    private bool HasQuarantine() => Selected is not null && _quarantineExists;

    /// <summary>Hapus permanen isi karantina satu sesi (konfirmasi; tombol merah, bukan tombol Enter).</summary>
    [RelayCommand(CanExecute = nameof(CanPurge))]
    private void Purge()
    {
        var session = Selected!;
        string folder = _quarantine.SessionFolder(session.Session.Id);
        string size = IndonesianFormat.Size(_sizes.Measure(folder) ?? 0);
        if (!_dialogs.ConfirmPurge(session.Title, size))
        {
            return;
        }

        if (_quarantine.PurgeSession(session.Session.Id))
        {
            // Catatan di jurnal supaya jelas kenapa sesi ini tidak bisa dibatalkan lagi.
            _journal.FinishSession(session.Session.Id, session.Session.Status, Strings.Get("History_PurgedNote"));
            NoticeText = Strings.Get("History_Purged");
        }

        Load();
    }

    private bool CanPurge() => !IsWorking && HasQuarantine() && Selected!.Session.Status != JournalSessionStatus.Running;
}

/// <summary>Konfirmasi yang dibutuhkan menu Riwayat (diganti saat tes).</summary>
/// <param name="ConfirmUndo">Judul sesi → setuju?</param>
/// <param name="ConfirmPurge">Judul sesi, ukuran → setuju hapus permanen?</param>
public sealed record HistoryDialogs(Func<string, bool> ConfirmUndo, Func<string, string, bool> ConfirmPurge);

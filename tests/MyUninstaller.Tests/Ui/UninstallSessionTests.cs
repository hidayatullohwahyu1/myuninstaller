using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.Logging.Abstractions;
using MyUninstaller.App.Services;
using MyUninstaller.App.ViewModels;
using MyUninstaller.App.Views;
using MyUninstaller.Core.Models;
using MyUninstaller.Core.Safety;
using MyUninstaller.Core.Scanning;
using MyUninstaller.Core.Uninstall;

namespace MyUninstaller.Tests.Ui;

public class UninstallSessionTests
{
    private sealed class FakeRunner : IProcessRunner
    {
        public List<string> Ran { get; } = [];

        public Task<ProcessRunResult> RunAsync(UninstallCommand command, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Ran.Add(command.FileName);
            return Task.FromResult(new ProcessRunResult(0, false, false, TimeSpan.FromSeconds(1)));
        }
    }

    private sealed class FakeStore : IStorePackageRemover
    {
        public Task<ProcessRunResult> RemoveAsync(string packageFullName, CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessRunResult(0, false, false, TimeSpan.Zero));
    }

    /// <summary>Entri "hilang" setelah dijalankan, kecuali program bernama "Bandel".</summary>
    private sealed class FakeProbe : IInstallStateProbe
    {
        public bool EntryExists(InstalledProgram program) => program.DisplayName == "Bandel";

        public bool FolderHasFiles(string path) => false;

        public bool FileExists(string path) => true;
    }

    private sealed class FakeProcesses(params RunningProcess[] running) : IProcessInspector
    {
        public List<int> Killed { get; } = [];

        public IReadOnlyList<RunningProcess> GetRunningProcesses() => running;

        public bool TryKill(RunningProcess process)
        {
            Killed.Add(process.ProcessId);
            return true;
        }
    }

    /// <summary>Jurnal di memori untuk memeriksa urutan pencatatan.</summary>
    internal sealed class MemoryJournal : ISessionJournal
    {
        private long _next;

        public List<JournalAction> Actions { get; } = [];

        public List<(string Id, JournalSessionStatus Status)> Finished { get; } = [];

        public string StartSession(string mode, string title) => "sesi-uji";

        public void FinishSession(string sessionId, JournalSessionStatus status, string? note = null) => Finished.Add((sessionId, status));

        public long LogPending(string sessionId, JournalActionKind kind, string target, string? backupPath = null)
        {
            Actions.Add(new JournalAction(++_next, sessionId, kind, target, backupPath, JournalActionStatus.Pending, null, DateTimeOffset.Now));
            return _next;
        }

        public void Complete(long actionId, JournalActionStatus status, string? detail = null, string? backupPath = null)
        {
            int i = Actions.FindIndex(a => a.Id == actionId);
            Actions[i] = Actions[i] with { Status = status, Detail = detail ?? Actions[i].Detail, BackupPath = backupPath ?? Actions[i].BackupPath };
        }

        public IReadOnlyList<JournalSession> GetSessions(int limit = 200) => [];

        public IReadOnlyList<JournalAction> GetActions(string sessionId) => Actions;

        public IReadOnlyList<JournalSession> GetUnfinishedSessions() => [];
    }

    private sealed class NoQuarantine : IQuarantineService
    {
        public List<string> Moved { get; } = [];

        public QuarantineResult Quarantine(string sessionId, string path, CancellationToken cancellationToken = default)
        {
            Moved.Add(path);
            return new QuarantineResult(QuarantineStatus.Moved, Path.Combine(@"C:\karantina", Path.GetFileName(path)), 100);
        }

        public bool Restore(string quarantinePath, string originalPath) => true;

        public int PurgeExpired(int retentionDays) => 0;

        public bool PurgeSession(string sessionId) => true;

        public string SessionFolder(string sessionId) => Path.Combine(Path.GetTempPath(), sessionId);
    }

    private sealed class NoRegistry : IRegistryBackupService
    {
        public bool KeyExists(string keyPath) => true;

        public string? Export(string sessionId, string keyPath) => Path.Combine(Path.GetTempPath(), "karantina-uji", "registri", "001.reg");

        public bool DeleteKey(string keyPath) => true;

        public bool Import(string regFile) => true;

        public bool ValueExists(string keyPath, string valueName) => true;

        public bool DeleteValue(string keyPath, string valueName) => true;
    }

    private static ProgramItemViewModel Item(string name, string? location = null) => new(new InstalledProgram
    {
        Id = "Registry64:" + name,
        DisplayName = name,
        Source = ProgramSource.Registry64,
        UninstallString = $@"""D:\Apps\{name}\uninst.exe""",
        InstallLocation = location ?? $@"D:\Apps\{name}",
        RegistryKeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + name,
    }, new DateOnly(2026, 11, 14));

    private static UninstallSessionViewModel Session(
        FakeRunner runner,
        FakeProcesses processes,
        RunningProcessChoice choice,
        params ProgramItemViewModel[] items) =>
        Session(runner, processes, choice, new MemoryJournal(), UninstallMode.Normal, null, new NoQuarantine(), items);

    private static UninstallSessionViewModel Session(
        FakeRunner runner,
        FakeProcesses processes,
        RunningProcessChoice choice,
        MemoryJournal journal,
        UninstallMode mode,
        Func<string, CancellationToken, Task<RestorePointStatus>>? restorePoint,
        NoQuarantine quarantine,
        params ProgramItemViewModel[] items)
    {
        // Folder D:\Apps\* tidak ada di mesin uji; untuk Hapus paksa folder dianggap ada.
        var protectedPaths = new ProtectedPaths([], []);
        var services = new UninstallSessionServices(
            new UninstallRunner(runner, new FakeStore(), new FakeProbe(), new InstallLocationRules([])),
            processes,
            journal,
            new SafeRemover(protectedPaths, quarantine, new NoRegistry(), journal),
            program => ForceUninstallPlanner.Plan(program, new InstallLocationRules([]), protectedPaths, _ => true),
            restorePoint);

        return new UninstallSessionViewModel(items, mode, services, (_, _) => choice, () => true, NullLogger.Instance);
    }

    [StaFact]
    public async Task Queue_continues_after_a_failure_and_summarizes()
    {
        var runner = new FakeRunner();
        var session = Session(runner, new FakeProcesses(), RunningProcessChoice.Ignore, Item("Satu"), Item("Bandel"), Item("Tiga"));

        await session.RunAsync();

        session.Items.Select(i => i.Outcome).Should().Equal(UninstallOutcome.Success, UninstallOutcome.Failed, UninstallOutcome.Success);
        runner.Ran.Should().HaveCount(3);
        session.IsFinished.Should().BeTrue();
        session.IsRunning.Should().BeFalse();
        session.AnyRemoved.Should().BeTrue();
        session.SummaryText.Should().Be("2 berhasil · 0 sebagian · 1 gagal");
        session.NoteText.Should().BeNull("tanpa pemindai sisa dan tanpa titik pemulihan tidak ada catatan");
    }

    [StaFact]
    public async Task Every_uninstaller_run_is_journaled_before_and_after()
    {
        var journal = new MemoryJournal();
        var session = Session(new FakeRunner(), new FakeProcesses(), RunningProcessChoice.Ignore, journal, UninstallMode.Normal, null, new NoQuarantine(), Item("Satu"));

        await session.RunAsync();

        journal.Actions.Should().ContainSingle().Which.Should().Match<JournalAction>(a =>
            a.Kind == JournalActionKind.RunUninstaller && a.Status == JournalActionStatus.Done);
        journal.Finished.Should().Equal(("sesi-uji", JournalSessionStatus.Completed));
    }

    [StaFact]
    public async Task Throttled_restore_point_continues_and_informs_user()
    {
        var journal = new MemoryJournal();
        string? description = null;
        var session = Session(new FakeRunner(), new FakeProcesses(), RunningProcessChoice.Ignore, journal, UninstallMode.Normal,
            (text, _) =>
            {
                description = text;
                return Task.FromResult(RestorePointStatus.Throttled);
            },
            new NoQuarantine(), Item("Satu"));

        await session.RunAsync();

        description.Should().Be("My Uninstaller! - Satu");
        journal.Actions[0].Should().Match<JournalAction>(a => a.Kind == JournalActionKind.RestorePoint && a.Status == JournalActionStatus.Skipped);
        session.Items[0].Outcome.Should().Be(UninstallOutcome.Success);
        session.NoteText.Should().Contain("24 jam");
    }

    [StaFact]
    public async Task Force_mode_skips_uninstaller_and_uses_quarantine()
    {
        var runner = new FakeRunner();
        var journal = new MemoryJournal();
        var quarantine = new NoQuarantine();

        // SafeRemover memeriksa folder sungguhan; NoQuarantine tidak memindahkan apa pun.
        string folder = Path.Combine(Path.GetTempPath(), "MyUninstallerTests", Guid.NewGuid().ToString("N"), "Rusak");
        Directory.CreateDirectory(folder);
        try
        {
            var session = Session(runner, new FakeProcesses(), RunningProcessChoice.Ignore, journal, UninstallMode.Force, null, quarantine, Item("Rusak", folder));

            await session.RunAsync();

            runner.Ran.Should().BeEmpty("Hapus paksa melewati penghapus instalan bawaan");
            quarantine.Moved.Should().Equal(folder);
            journal.Actions.Select(a => (a.Kind, a.Status)).Should().Equal(
                (JournalActionKind.QuarantinePath, JournalActionStatus.Done),
                (JournalActionKind.DeleteRegistryKey, JournalActionStatus.Done));
            session.Items[0].Outcome.Should().Be(UninstallOutcome.Success);
            session.Items[0].StatusText.Should().Be("Dipindahkan ke karantina: 1 folder/berkas, 1 kunci registri");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
        }
    }

    [StaFact]
    public async Task Running_program_can_be_skipped()
    {
        var runner = new FakeRunner();
        var processes = new FakeProcesses(new RunningProcess(42, "satu", @"D:\Apps\Satu\satu.exe"));
        var session = Session(runner, processes, RunningProcessChoice.Skip, Item("Satu"));

        await session.RunAsync();

        session.Items[0].Outcome.Should().Be(UninstallOutcome.Cancelled);
        runner.Ran.Should().BeEmpty();
        processes.Killed.Should().BeEmpty();
    }

    [StaFact]
    public async Task Running_program_can_be_closed_first()
    {
        var runner = new FakeRunner();
        var processes = new FakeProcesses(
            new RunningProcess(42, "satu", @"D:\Apps\Satu\satu.exe"),
            new RunningProcess(43, "lain", @"D:\Apps\Lain\lain.exe"));
        var session = Session(runner, processes, RunningProcessChoice.Kill, Item("Satu"));

        await session.RunAsync();

        processes.Killed.Should().Equal(42); // hanya proses di folder program
        runner.Ran.Should().ContainSingle();
        session.Items[0].Outcome.Should().Be(UninstallOutcome.Success);
    }
}

/// <summary>Memastikan XAML jendela baru bisa dimuat dengan sumber daya tema aplikasi.</summary>
public class WindowXamlTests
{
    private static readonly object Gate = new();

    internal static void EnsureApplicationResources()
    {
        lock (Gate)
        {
            if (Application.Current is not null)
            {
                return;
            }

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/MyUninstaller;component/Themes/Colors.Dark.xaml") });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/MyUninstaller;component/Themes/Controls.xaml") });
            foreach (string key in new[] { "AccentBrush", "AccentHoverBrush", "AccentTextBrush", "AccentSubtleBrush" })
            {
                app.Resources[key] = new SolidColorBrush(Colors.DodgerBlue);
            }
        }
    }

    [StaFact]
    public void Dialogs_load()
    {
        EnsureApplicationResources();

        var mode = new UninstallModeDialog("Hapus instalan Uji", UninstallMode.Super);
        mode.Title.Should().Be("Pilih mode hapus instalan");
        mode.Close();

        var hunter = new HunterWindow(new HunterProbe(NullLogger<HunterProbe>.Instance), () => [], _ => { }, _ => { });
        hunter.Title.Should().Be("Mode Pemburu");
        hunter.Close();

        var services = new UninstallSessionServices(
            new UninstallRunner(null!, null!, null!, new InstallLocationRules([])), null!, null!, null!, _ => [], null);
        var session = new UninstallSessionViewModel(
            [], UninstallMode.Normal, services, (_, _) => RunningProcessChoice.Skip, () => false, NullLogger.Instance);
        var progress = new UninstallProgressWindow(session);
        progress.Title.Should().Be("Hapus instalan");
    }
}

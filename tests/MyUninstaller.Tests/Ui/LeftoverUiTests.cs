using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using MyUninstaller.App.ViewModels;
using MyUninstaller.App.Views;
using MyUninstaller.Core.Leftovers;
using MyUninstaller.Core.Models;
using MyUninstaller.Core.Safety;
using MyUninstaller.Core.Scanning;
using MyUninstaller.Core.Uninstall;

namespace MyUninstaller.Tests.Ui;

public sealed class LeftoverUiTests : IDisposable
{
    private readonly List<string> _tempRoots = [];

    public void Dispose()
    {
        foreach (string root in _tempRoots.Where(Directory.Exists))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static LeftoverItem L(string location, LeftoverEvidence evidence, LeftoverKind kind = LeftoverKind.Folder, string program = "Uji") => new()
    {
        Kind = kind,
        Location = location,
        Evidence = evidence,
        Score = ConfidenceScorer.Score(evidence, kind),
        ProgramName = program,
        SizeBytes = 1024,
    };

    private sealed class FakeScanner(params LeftoverItem[] items) : ILeftoverScanner
    {
        public List<(string Program, int StillInstalled, bool Deep)> Calls { get; } = [];

        public Task<IReadOnlyList<LeftoverItem>> ScanAsync(InstalledProgram program, IReadOnlyList<InstalledProgram> stillInstalled, bool deep, CancellationToken cancellationToken = default)
        {
            Calls.Add((program.DisplayName, stillInstalled.Count, deep));
            return Task.FromResult<IReadOnlyList<LeftoverItem>>(items);
        }
    }

    /// <summary>Penghapus aman dengan karantina palsu (tidak menyentuh berkas).</summary>
    private sealed class RecordingQuarantine : IQuarantineService
    {
        public List<string> Moved { get; } = [];

        public QuarantineResult Quarantine(string sessionId, string path, CancellationToken cancellationToken = default)
        {
            Moved.Add(path);
            return new QuarantineResult(QuarantineStatus.Moved, path + ".karantina", 1024);
        }

        public bool Restore(string quarantinePath, string originalPath) => true;

        public int PurgeExpired(int retentionDays) => 0;

        public bool PurgeSession(string sessionId) => true;

        public string SessionFolder(string sessionId) => Path.Combine(Path.GetTempPath(), sessionId);
    }

    private sealed class NullRegistry : IRegistryBackupService
    {
        public bool KeyExists(string keyPath) => false;

        public string? Export(string sessionId, string keyPath) => null;

        public bool DeleteKey(string keyPath) => false;

        public bool Import(string regFile) => true;

        public bool ValueExists(string keyPath, string valueName) => false;

        public bool DeleteValue(string keyPath, string valueName) => false;
    }

    private string TempFolder(string name)
    {
        string root = Path.Combine(Path.GetTempPath(), "MyUninstallerTests", Guid.NewGuid().ToString("N"));
        _tempRoots.Add(root);
        string path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    // ------------------------------------------------------------------ sesi

    private sealed class OkRunner : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(UninstallCommand command, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessRunResult(0, false, false, TimeSpan.FromSeconds(1)));
    }

    private sealed class GoneProbe : IInstallStateProbe
    {
        public bool EntryExists(InstalledProgram program) => false;

        public bool FolderHasFiles(string path) => false;

        public bool FileExists(string path) => true;
    }

    private sealed class NoProcesses : IProcessInspector
    {
        public IReadOnlyList<RunningProcess> GetRunningProcesses() => [];

        public bool TryKill(RunningProcess process) => false;
    }

    private sealed class NoStore : IStorePackageRemover
    {
        public Task<ProcessRunResult> RemoveAsync(string packageFullName, CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessRunResult(0, false, false, TimeSpan.Zero));
    }

    private static (UninstallSessionViewModel Session, FakeScanner Scanner, RecordingQuarantine Quarantine) Session(UninstallMode mode, params LeftoverItem[] found)
    {
        var target = new ProgramItemViewModel(new InstalledProgram
        {
            Id = "Registry64:Uji",
            DisplayName = "Uji",
            Source = ProgramSource.Registry64,
            UninstallString = @"""D:\Apps\Uji\uninst.exe""",
        }, new DateOnly(2026, 11, 14));
        var other = new InstalledProgram { Id = "Registry64:Lain", DisplayName = "Lain", Source = ProgramSource.Registry64 };

        var journal = new UninstallSessionTests.MemoryJournal();
        var quarantine = new RecordingQuarantine();
        var scanner = new FakeScanner(found);
        var services = new UninstallSessionServices(
            new UninstallRunner(new OkRunner(), new NoStore(), new GoneProbe(), new InstallLocationRules([])),
            new NoProcesses(),
            journal,
            new SafeRemover(new ProtectedPaths([], []), quarantine, new NullRegistry(), journal),
            _ => [],
            null,
            scanner,
            [target.Model, other]);

        return (new UninstallSessionViewModel([target], mode, services, (_, _) => RunningProcessChoice.Ignore, () => true, NullLogger.Instance), scanner, quarantine);
    }

    [StaFact]
    public async Task Normal_mode_collects_leftovers_for_review()
    {
        string safe = TempFolder("AppDataUji");
        var (session, scanner, quarantine) = Session(UninstallMode.Normal,
            L(safe, LeftoverEvidence.ExactName), L(@"D:\x\Penerbit", LeftoverEvidence.PublisherOnly));

        await session.RunAsync();

        scanner.Calls.Should().Equal(("Uji", 1, false)); // program lain dianggap masih terpasang
        quarantine.Moved.Should().BeEmpty("mode Normal tidak menghapus otomatis");
        session.Leftovers.Should().HaveCount(2);
        session.HasLeftoversToReview.Should().BeTrue();
        session.ShowPlainClose.Should().BeFalse();
        session.ReviewText.Should().Be("Tinjau sisa instalan (2)");
        session.Items[0].StatusText.Should().EndWith("2 sisa instalan ditemukan");
    }

    [StaFact]
    public async Task Super_mode_scans_deep()
    {
        var (session, scanner, _) = Session(UninstallMode.Super);

        await session.RunAsync();

        scanner.Calls.Single().Deep.Should().BeTrue();
        session.ShowPlainClose.Should().BeTrue();
    }

    [StaFact]
    public async Task Safe_mode_auto_removes_only_safe_group()
    {
        string safe = TempFolder("AppDataUji");
        string maybe = TempFolder("Penerbit");
        var (session, _, quarantine) = Session(UninstallMode.Safe,
            L(safe, LeftoverEvidence.ExactName), L(maybe, LeftoverEvidence.PublisherOnly));

        await session.RunAsync();

        quarantine.Moved.Should().Equal(safe);
        session.Leftovers.Should().BeEmpty();
        session.Items[0].StatusText.Should().EndWith("1 sisa berlabel Aman dibersihkan otomatis");
    }

    [StaFact]
    public async Task Builtin_mode_does_not_scan()
    {
        var (session, scanner, _) = Session(UninstallMode.Builtin, L(@"D:\x", LeftoverEvidence.ExactName));

        await session.RunAsync();

        scanner.Calls.Should().BeEmpty();
        session.Steps[3].State.Should().Be(StepState.Skipped);
    }

    // ------------------------------------------------------------------ layar hasil

    private static LeftoverResultsViewModel Results(Func<int, bool> confirmRisky, RecordingQuarantine quarantine, params LeftoverItem[] items)
    {
        var journal = new UninstallSessionTests.MemoryJournal();
        var remover = new SafeRemover(new ProtectedPaths([], []), quarantine, new NullRegistry(), journal);
        var restorer = new SessionRestorer(journal, quarantine, new NullRegistry());
        return new LeftoverResultsViewModel(items, "sesi-uji", remover, restorer, journal, confirmRisky, () => true, NullLogger.Instance);
    }

    [StaFact]
    public void Only_safe_items_are_checked_and_doubtful_hidden_by_default()
    {
        var vm = Results(_ => true, new RecordingQuarantine(),
            L(@"D:\a", LeftoverEvidence.InstallLocation),
            L(@"D:\b", LeftoverEvidence.PublisherOnly),
            L(@"D:\c", LeftoverEvidence.SimilarName));

        vm.Items.Select(i => i.IsChecked).Should().Equal(true, false, false);
        vm.View.Cast<LeftoverItemViewModel>().Should().HaveCount(2, "Ragu disembunyikan");
        vm.ShowDoubtfulText.Should().Be("Tampilkan yang ragu (1)");
        vm.CheckedText.Should().Be("1 dicentang · 1 KB");

        vm.ShowDoubtful = true;
        vm.View.Cast<LeftoverItemViewModel>().Should().HaveCount(3);
    }

    [StaFact]
    public async Task Risky_items_need_second_confirmation()
    {
        string safe = TempFolder("Aman");
        string maybe = TempFolder("Mungkin");
        int asked = 0;
        var quarantine = new RecordingQuarantine();
        var vm = Results(count =>
        {
            asked = count;
            return false; // pengguna membatalkan
        }, quarantine, L(safe, LeftoverEvidence.ExactName), L(maybe, LeftoverEvidence.PublisherOnly));

        vm.Items[1].IsChecked = true;
        await vm.RemoveCheckedCommand.ExecuteAsync(null);

        asked.Should().Be(1);
        quarantine.Moved.Should().BeEmpty();
        vm.IsDone.Should().BeFalse();
    }

    [StaFact]
    public async Task Remove_checked_summarizes_and_can_be_undone()
    {
        string safe = TempFolder("Aman");
        var quarantine = new RecordingQuarantine();
        var vm = Results(_ => true, quarantine, L(safe, LeftoverEvidence.ExactName), L(@"D:\tidak-dicentang", LeftoverEvidence.PublisherOnly));

        await vm.RemoveCheckedCommand.ExecuteAsync(null);

        quarantine.Moved.Should().Equal(safe);
        vm.IsDone.Should().BeTrue();
        vm.RemovedCount.Should().Be(1);
        vm.SummaryText.Should().Be("Program berhasil dihapus. 1 sisa dibersihkan.");
        vm.SummaryDetailText.Should().StartWith("1 berkas/folder · 0 entri registri · 0 layanan/tugas · 1 KB");
        vm.Items[0].ResultText.Should().Be("Dipindahkan ke karantina");
        vm.Items[0].IsEditable.Should().BeFalse();
        vm.UndoCommand.CanExecute(null).Should().BeTrue();

        await vm.UndoCommand.ExecuteAsync(null);

        vm.IsUndone.Should().BeTrue();
        vm.SummaryText.Should().Be("1 berkas/folder dan 0 entri dipulihkan.");
    }

    [StaFact]
    public void Results_window_loads()
    {
        WindowXamlTests.EnsureApplicationResources();
        var vm = Results(_ => true, new RecordingQuarantine(), L(@"D:\a", LeftoverEvidence.InstallLocation));

        var window = new LeftoverResultsWindow(vm);

        window.Title.Should().Be("Sisa instalan");
        window.Close();
    }

    // ------------------------------------------------------------------ teks

    [Fact]
    public void Every_enum_used_for_dynamic_text_has_a_resx_key()
    {
        string resx = Path.Combine(FindRepoRoot(), "src", "MyUninstaller.App", "Resources", "Strings.id.resx");
        var keys = XDocument.Load(resx).Root!.Elements("data").Select(e => (string)e.Attribute("name")!).ToHashSet();

        var expected = Enum.GetNames<LeftoverKind>().Select(n => "Kind_" + n)
            .Concat(Enum.GetNames<LeftoverEvidence>().Select(n => "Evidence_" + n))
            .Concat(Enum.GetNames<RemovalStatus>().Select(n => "Result_" + n))
            .Concat(Enum.GetNames<RestorePointStatus>().Select(n => "RestorePointResult_" + n))
            .Concat(Enum.GetNames<UninstallOutcome>().Select(n => "OutcomeShort_" + n))
            .Concat(Enum.GetNames<JournalSessionStatus>().Select(n => "SessionStatus_" + n))
            .Concat(Enum.GetNames<JournalActionKind>().Select(n => "ActionKind_" + n))
            .Concat(Enum.GetNames<JournalActionStatus>().Select(n => "ActionStatus_" + n))
            .Concat(Enum.GetNames<UninstallMode>().Select(n => "SessionMode_" + n))
            .Append("SessionMode_BrokenEntry");

        expected.Except(keys).Should().BeEmpty();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MyUninstaller.sln")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }
}

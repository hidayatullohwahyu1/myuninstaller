using Microsoft.Extensions.Logging.Abstractions;
using MyUninstaller.App.Services;
using MyUninstaller.App.ViewModels;
using MyUninstaller.Core.Models;
using MyUninstaller.Core.Portable;
using MyUninstaller.Core.Safety;
using MyUninstaller.Core.Startup;
using MyUninstaller.Core.Usage;
using MyUninstaller.Infrastructure.FileSystem;
using MyUninstaller.Infrastructure.Portable;
using MyUninstaller.Infrastructure.Safety;
using MyUninstaller.Tests.Safety;

namespace MyUninstaller.Tests.Startup;

public class StartupAssessmentTests
{
    private static InstalledProgram P(string name, string? publisher = null, string? location = null) => new()
    {
        Id = name,
        DisplayName = name,
        Publisher = publisher,
        Source = ProgramSource.Registry32,
        InstallLocation = location,
    };

    private static StartupEntry E(string exe, string? product = null, string? company = null) => new()
    {
        Name = Path.GetFileNameWithoutExtension(exe),
        Command = $"\"{exe}\"",
        Source = StartupSource.RunMachine32,
        Location = @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
        ApprovalName = Path.GetFileNameWithoutExtension(exe),
        IsEnabled = true,
        ExecutablePath = exe,
        ProductName = product,
        CompanyName = company,
    };

    [Fact]
    public void Owner_by_product_name_when_install_location_is_missing()
    {
        // Kasus nyata: Epson Event Manager tidak mencatat InstallLocation.
        var epson = P("Epson Event Manager", "Seiko Epson Corporation");
        var entry = E(@"C:\Program Files (x86)\Epson Software\Event Manager\EEventManager.exe", "EPSON Event Manager", "SEIKO EPSON CORPORATION");

        StartupAssessment.FindOwner(entry, [P("Lain"), epson]).Should().Be(epson);
    }

    [Fact]
    public void Owner_by_path_first_then_no_guess_for_generic_or_other_publisher()
    {
        var app = P("Contoh App", "Contoh", @"D:\Apps\Contoh");
        StartupAssessment.FindOwner(E(@"D:\Apps\Contoh\tray.exe"), [app]).Should().Be(app);

        StartupAssessment.FindOwner(E(@"C:\X\x.exe", "Update"), [P("Update Helper Pro")]).Should().BeNull("nama umum tidak dipakai");
        StartupAssessment.FindOwner(E(@"C:\X\x.exe", "Creative Cloud", "Penerbit Lain"), [P("Adobe Creative Cloud", "Adobe Inc.")])
            .Should().BeNull("penerbit berbeda");
        StartupAssessment.FindOwner(E(@"C:\X\x.exe", "Adobe Creative Cloud", "Adobe Inc."), [P("Adobe Creative Cloud", "Adobe Systems Incorporated")])
            .Should().NotBeNull();
    }

    [Fact]
    public void Status_missing_unknown_windows_owned()
    {
        // Kasus nyata: AppData\Roaming\RepairCang\Tao Raiqsuv Utils\ueli.exe sudah dihapus, entri Run tertinggal.
        var missing = E(@"C:\Users\u\AppData\Roaming\RepairCang\Tao Raiqsuv Utils\ueli.exe");
        StartupAssessment.Classify(missing, null, _ => false).Should().Be(StartupStatus.MissingFile);
        StartupAssessment.Classify(missing, null, _ => true).Should().Be(StartupStatus.UnknownOwner);
        StartupAssessment.Classify(E(@"C:\Windows\System32\SecurityHealthSystray.exe"), null, _ => true, @"C:\Windows")
            .Should().Be(StartupStatus.Windows);
        StartupAssessment.Classify(E(@"D:\A\a.exe"), P("A"), _ => true).Should().Be(StartupStatus.Owned);
    }

    [Theory]
    [InlineData(@"C:\Users\u\AppData\Roaming\RepairCang\x\ueli.exe", true)]
    [InlineData(@"C:\Users\u\AppData\Local\dyad\app.exe", true)]
    [InlineData(@"C:\Users\u\AppData\Local\Programs\App\app.exe", false)]
    [InlineData(@"C:\Program Files\App\app.exe", false)]
    public void Runs_from_app_data(string exe, bool expected) =>
        StartupAssessment.RunsFromAppData(exe, @"C:\Users\u\AppData\Roaming", @"C:\Users\u\AppData\Local").Should().Be(expected);
}

public sealed class StartupCleanupTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "MyUninstallerTests", Guid.NewGuid().ToString("N"));

    private (StartupCleanupService Service, SqliteSessionJournal Journal) Create()
    {
        var protectedPaths = new ProtectedPaths([], []);
        var journal = new SqliteSessionJournal(Path.Combine(_work, "riwayat.db"), new FixedClock(DateTimeOffset.Now));
        var quarantine = new QuarantineService(Path.Combine(_work, "Karantina"), protectedPaths, NullLogger<QuarantineService>.Instance);
        var registry = new RegistryBackupService(Path.Combine(_work, "Karantina"), NullLogger<RegistryBackupService>.Instance);
        return (new StartupCleanupService(new SafeRemover(protectedPaths, quarantine, registry, journal), journal,
            NullLogger<StartupCleanupService>.Instance), journal);
    }

    private StartupEntry ShortcutEntry(string target)
    {
        string lnk = Path.Combine(_work, "Startup", "Ueli.lnk");
        Directory.CreateDirectory(Path.GetDirectoryName(lnk)!);
        File.WriteAllText(lnk, "lnk");
        return new StartupEntry
        {
            Name = "Ueli",
            Command = target,
            Source = StartupSource.StartupFolderUser,
            Location = lnk,
            ApprovalName = "Ueli.lnk",
            IsEnabled = false,
            ExecutablePath = target,
        };
    }

    [Fact]
    public void Entry_whose_file_is_gone_is_quarantined_and_journaled()
    {
        var (service, journal) = Create();
        var entry = ShortcutEntry(Path.Combine(_work, "hilang", "ueli.exe"));

        service.RemoveMissing(entry).Should().BeTrue();

        File.Exists(entry.Location).Should().BeFalse();
        var session = journal.GetSessions().Single();
        session.Mode.Should().Be("Startup");
        journal.GetActions(session.Id).Single().Kind.Should().Be(JournalActionKind.QuarantinePath);
    }

    [Fact]
    public void Entry_whose_file_still_exists_is_never_removed()
    {
        string exe = Path.Combine(_work, "ada", "app.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "exe");
        var (service, journal) = Create();
        var entry = ShortcutEntry(exe);

        service.RemoveMissing(entry).Should().BeFalse();
        File.Exists(entry.Location).Should().BeTrue();
        journal.GetSessions().Should().BeEmpty();
    }

    [Theory]
    [InlineData(StartupSource.RunUser, RemovalTargetKind.RegistryValue)]
    [InlineData(StartupSource.RunMachine32, RemovalTargetKind.RegistryValue)]
    [InlineData(StartupSource.StartupFolderCommon, RemovalTargetKind.Path)]
    [InlineData(StartupSource.ScheduledTask, RemovalTargetKind.ScheduledTask)]
    public void Target_kind_per_source(StartupSource source, RemovalTargetKind kind) =>
        StartupCleanupService.TargetOf(new StartupEntry
        {
            Name = "x", Command = "x", Source = source, Location = @"\x", ApprovalName = "x", IsEnabled = true,
        })!.Kind.Should().Be(kind);

    [Fact]
    public void View_model_flags_and_allows_removal_only_for_missing_files()
    {
        var entries = new[]
        {
            new StartupEntry { Name = "Ada", Command = "a", Source = StartupSource.RunUser, Location = "k", ApprovalName = "Ada", IsEnabled = true,
                ExecutablePath = typeof(StartupCleanupTests).Assembly.Location },
            new StartupEntry { Name = "Hilang", Command = "b", Source = StartupSource.RunUser, Location = "k", ApprovalName = "Hilang", IsEnabled = false,
                ExecutablePath = Path.Combine(_work, "tidak-ada.exe") },
        };
        var removed = new List<string>();
        var vm = new StartupViewModel(new FixedRepository(entries), new NullShell(), () => [], null, NullLogger.Instance,
            entry => { removed.Add(entry.Name); return true; });

        var present = vm.Items.Single(i => i.Name == "Ada");
        var missing = vm.Items.Single(i => i.Name == "Hilang");
        present.IsUnknownOwner.Should().BeTrue();
        missing.IsMissing.Should().BeTrue();
        missing.StatusText.Should().StartWith("Berkasnya sudah tidak ada.");
        vm.SummaryText.Should().Be("2 program awal · 1 aktif · 1 tanpa pemilik · 1 berkas hilang");

        vm.Selected = present;
        vm.RemoveEntryCommand.CanExecute(null).Should().BeFalse("berkasnya masih ada: cukup dinonaktifkan");
        vm.Selected = missing;
        vm.RemoveEntryCommand.Execute(null);
        removed.Should().Equal("Hilang");
    }

    private sealed class FixedRepository(IReadOnlyList<StartupEntry> entries) : IStartupRepository
    {
        public IReadOnlyList<StartupEntry> GetAll() => entries;

        public bool SetEnabled(StartupEntry entry, bool enabled) => true;
    }

    private sealed class NullShell : IShellService
    {
        public bool OpenFolder(string path) => true;

        public bool OpenUrl(string url) => true;

        public void CopyText(string text)
        {
        }

        public bool OpenRegistryEditor(string keyPath) => true;
    }

    public void Dispose()
    {
        if (Directory.Exists(_work))
        {
            Directory.Delete(_work, recursive: true);
        }
    }
}

/// <summary>Program tak terdaftar di AppData (folder sementara sebagai Roaming).</summary>
[Trait("Category", "Integration")]
public sealed class AppDataScanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MyUninstallerTests", Guid.NewGuid().ToString("N"));

    private sealed class NoUsage : IUsageHistory
    {
        public UsageIndex Load() => UsageIndex.Empty;

        public IReadOnlyList<string> ExecutablesIn(string installLocation) => [];
    }

    private void File(string relative)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[10]);
    }

    [Fact]
    public void Unregistered_app_data_programs_are_found_and_flagged_but_components_of_installed_programs_are_not()
    {
        string roaming = Path.Combine(_root, "Roaming");
        File(@"Roaming\utorrent\updates\3.6.0_46716.exe");
        File(@"Roaming\RepairCang\Tao Raiqsuv Utils\ueli.exe");
        File(@"Roaming\Claude-NativeHost\5.11492.5\claude-native.exe"); // komponen "Claude" yang terpasang
        File(@"Roaming\Android\Sdk\emulator\emulator.exe");               // milik "Android Studio"
        File(@"Roaming\Microsoft\Teams\Update.exe");                      // Microsoft: dilewati
        File(@"Roaming\Dalam\a\b\c\terlalu-dalam.exe");                   // di luar kedalaman AppData

        InstalledProgram[] installed =
        [
            new() { Id = "c", DisplayName = "Claude", Source = ProgramSource.Store },
            new() { Id = "a", DisplayName = "Android Studio", Source = ProgramSource.Registry64 },
        ];

        var scanner = new PortableProgramScanner(new FolderSizeCalculator(), new NoUsage(), new ProtectedPaths([], []),
            NullLogger<PortableProgramScanner>.Instance, shortcutFolders: [], defaultRoots: [roaming], appDataRoots: [roaming]);

        var found = scanner.Scan(scanner.DefaultRoots, installed, CancellationToken.None);

        found.Select(p => Path.GetRelativePath(roaming, p.Folder)).Should().BeEquivalentTo(@"utorrent\updates", @"RepairCang\Tao Raiqsuv Utils");
        found.Should().OnlyContain(p => p.IsInAppData);
    }

    [Fact]
    public void Top_level_app_data_name_matches_installed_program()
    {
        var exe = new ExecutableInfo(@"C:\R\Claude-NativeHost\5.1\x.exe", 10, null, null, null, null);
        PortableRules.MatchesInstalledProgram("5.1", exe, [new() { Id = "c", DisplayName = "Claude", Source = ProgramSource.Store }], "Claude-NativeHost")
            .Should().BeTrue();
        PortableRules.MatchesInstalledProgram("5.1", exe, [new() { Id = "c", DisplayName = "Claude", Source = ProgramSource.Store }], "RepairCang")
            .Should().BeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

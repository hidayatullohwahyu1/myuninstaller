using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using MyUninstaller.App.ViewModels;
using MyUninstaller.App.Views;
using MyUninstaller.Core.Leftovers;
using MyUninstaller.Core.Models;
using MyUninstaller.Core.Monitoring;
using MyUninstaller.Core.Safety;
using MyUninstaller.Infrastructure.Leftovers;
using MyUninstaller.Infrastructure.Monitoring;
using MyUninstaller.Tests.Safety;
using MyUninstaller.Tests.Ui;

namespace MyUninstaller.Tests.Monitoring;

public class InstallTraceRulesTests
{
    private const string Uninstall = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static TraceItem F(string path) => new(LeftoverKind.Folder, path);

    private static TraceItem K(string path) => new(LeftoverKind.RegistryKey, path);

    [Fact]
    public void Only_topmost_new_items_are_kept()
    {
        var before = new SystemSnapshot([F(@"C:\PF\Lama"), K(@"HKEY_CURRENT_USER\Software\Lama"), new TraceItem(LeftoverKind.RegistryValue, @"HKCU\Run", "Lama")]);
        var after = new SystemSnapshot(
        [
            .. before.Items,
            F(@"C:\PF\Baru"), F(@"C:\PF\Baru\bin"), new TraceItem(LeftoverKind.File, @"C:\PF\Baru\a.exe"),
            F(@"C:\PF\Lama\Plugin"),
            K(@"HKEY_CURRENT_USER\Software\Baru"), K(@"HKEY_CURRENT_USER\Software\Baru\Pengaturan"),
            new TraceItem(LeftoverKind.RegistryValue, @"HKEY_CURRENT_USER\Software\Baru", "x"),
            new TraceItem(LeftoverKind.RegistryValue, @"HKCU\Run", "Baru"),
            new TraceItem(LeftoverKind.Service, "LayananBaru"),
        ]);

        InstallTraceRules.Added(before, after).Should().BeEquivalentTo(
        [
            F(@"C:\PF\Baru"),
            F(@"C:\PF\Lama\Plugin"),
            K(@"HKEY_CURRENT_USER\Software\Baru"),
            new TraceItem(LeftoverKind.RegistryValue, @"HKCU\Run", "Baru"),
            new TraceItem(LeftoverKind.Service, "LayananBaru"),
        ]);
    }

    [Fact]
    public void Comparison_ignores_case()
    {
        var before = new SystemSnapshot([F(@"C:\PF\App")]);
        InstallTraceRules.Added(before, new SystemSnapshot([F(@"c:\pf\APP\")])).Should().BeEmpty();
    }

    [Fact]
    public void Uninstall_keys_and_program_matching()
    {
        TraceItem[] items = [K(Uninstall + @"\{GUID}"), K(Uninstall + @"\{GUID}\Sub"), K(@"HKEY_LOCAL_MACHINE\SOFTWARE\Vendor")];
        InstallTraceRules.UninstallKeysIn(items).Should().Equal(Uninstall + @"\{GUID}");

        var trace = new InstallTrace { Id = "a", Name = "Contoh App 2.1 (64-bit)", CreatedAt = DateTimeOffset.Now, Items = items, UninstallKeys = [Uninstall + @"\{GUID}"] };
        InstallTraceRules.Matches(trace, Program("Nama Lain", Uninstall + @"\{guid}")).Should().BeTrue("kunci Uninstall sama");
        InstallTraceRules.Matches(trace, Program("Contoh App 3.0", null)).Should().BeTrue("nama sama tanpa versi");
        InstallTraceRules.Matches(trace, Program("Contoh", null)).Should().BeFalse();
    }

    internal static InstalledProgram Program(string name, string? key, string? location = null, string? publisher = null) => new()
    {
        Id = name,
        DisplayName = name,
        Source = ProgramSource.Registry64,
        RegistryKeyPath = key,
        InstallLocation = location,
        Publisher = publisher,
    };
}

public class TraceAwareLeftoverScannerTests
{
    private sealed class Inner(params LeftoverItem[] items) : ILeftoverScanner
    {
        public Task<IReadOnlyList<LeftoverItem>> ScanAsync(InstalledProgram program, IReadOnlyList<InstalledProgram> stillInstalled, bool deep,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LeftoverItem>>(items);
    }

    private sealed class Store(params InstallTrace[] traces) : IInstallTraceStore
    {
        public IReadOnlyList<InstallTrace> GetAll() => traces;

        public void Save(InstallTrace trace) => throw new NotSupportedException();

        public void Delete(string id) => throw new NotSupportedException();
    }

    private sealed class Probe(params string[] missing) : ITraceItemProbe
    {
        public bool Exists(TraceItem item) => !missing.Contains(item.Location);
    }

    [Fact]
    public async Task Recorded_items_are_added_as_safe_but_never_from_other_programs()
    {
        var program = InstallTraceRulesTests.Program("Contoh App", @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Contoh");
        var other = InstallTraceRulesTests.Program("Alat Lain", null, location: @"C:\PF\Vendor\Alat Lain", publisher: "Vendor Bersama Inc.");
        var trace = new InstallTrace
        {
            Id = "a",
            Name = "Contoh App",
            CreatedAt = DateTimeOffset.Now,
            Items =
            [
                new TraceItem(LeftoverKind.Folder, @"C:\PF\Contoh"),
                new TraceItem(LeftoverKind.Folder, @"C:\PF\Vendor"), // memuat folder program lain
                new TraceItem(LeftoverKind.Folder, @"C:\Data\Vendor Bersama"), // nama penerbit program lain
                new TraceItem(LeftoverKind.RegistryKey, @"HKEY_CURRENT_USER\Software\Vendor Bersama"),
                new TraceItem(LeftoverKind.RegistryKey, @"HKEY_CURRENT_USER\Software\Contoh"),
                new TraceItem(LeftoverKind.File, @"C:\Users\u\Desktop\Contoh.lnk"),
                new TraceItem(LeftoverKind.Folder, @"C:\PF\SudahHilang"),
            ],
        };

        var existing = new LeftoverItem
        {
            Kind = LeftoverKind.RegistryKey,
            Location = @"HKEY_CURRENT_USER\Software\Contoh",
            Evidence = LeftoverEvidence.SimilarName,
            Score = 35,
            ProgramName = "Contoh App",
        };

        var scanner = new TraceAwareLeftoverScanner(new Inner(existing), new Store(trace), new Probe(@"C:\PF\SudahHilang"));
        var result = await scanner.ScanAsync(program, [program, other], deep: false);

        result.Select(i => i.Location).Should().BeEquivalentTo(
            @"C:\PF\Contoh", @"HKEY_CURRENT_USER\Software\Contoh", @"C:\Users\u\Desktop\Contoh.lnk");
        result.Should().OnlyContain(i => i.Evidence == LeftoverEvidence.RecordedAtInstall && i.Group == LeftoverGroup.Safe);
    }

    [Fact]
    public async Task Without_matching_trace_result_is_unchanged()
    {
        var item = new LeftoverItem { Kind = LeftoverKind.Folder, Location = @"C:\X", Evidence = LeftoverEvidence.ExactName, Score = 82, ProgramName = "X" };
        var scanner = new TraceAwareLeftoverScanner(new Inner(item), new Store(), new Probe());

        (await scanner.ScanAsync(InstallTraceRulesTests.Program("X", null), [], deep: false)).Should().Equal(item);
    }
}

/// <summary>Gambaran sungguhan pada folder sementara dan kunci HKCU uji (dibuat dan dihapus tes ini).</summary>
[Trait("Category", "Integration")]
public sealed class SystemSnapshotterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MyUninstallerTests", Guid.NewGuid().ToString("N"));
    private readonly string _regRoot = @"Software\MyUninstallerTests\" + Guid.NewGuid().ToString("N");

    private string FullKey(string sub) => @"HKEY_CURRENT_USER\" + _regRoot + @"\" + sub;

    [Fact]
    public void Detects_what_an_installer_adds()
    {
        string programFiles = Path.Combine(_root, "PF");
        string uninstall = _regRoot + @"\Microsoft\Windows\CurrentVersion\Uninstall";
        Directory.CreateDirectory(Path.Combine(programFiles, "Lama"));
        Registry.CurrentUser.CreateSubKey(_regRoot + @"\SOFTWARE\Lama")!.Dispose();
        Registry.CurrentUser.CreateSubKey(_regRoot + @"\Run")!.Dispose();
        Registry.CurrentUser.CreateSubKey(uninstall)!.Dispose();

        var roots = new LeftoverScanRoots
        {
            FolderRoots = [programFiles],
            UserDataRoots = [],
            ShortcutRoots = [],
            SoftwareRoots = [new RegistryRoot(RegistryHive.CurrentUser, _regRoot + @"\SOFTWARE")],
            RunKeys = [new RegistryRoot(RegistryHive.CurrentUser, _regRoot + @"\Run")],
            AppPathsKeys = [],
            ClsidKeys = [],
            DriveRoots = [],
        };
        var snapshotter = new SystemSnapshotter(roots, new ProtectedPaths([], [programFiles]), NullLogger<SystemSnapshotter>.Instance,
            [new RegistryRoot(RegistryHive.CurrentUser, uninstall)]);

        var before = snapshotter.Take(CancellationToken.None);

        // "Installer" bekerja.
        Directory.CreateDirectory(Path.Combine(programFiles, "Baru", "bin"));
        File.WriteAllText(Path.Combine(programFiles, "Baru", "bin", "app.exe"), "x");
        Registry.CurrentUser.CreateSubKey(_regRoot + @"\SOFTWARE\Baru\Pengaturan")!.Dispose();
        using (var run = Registry.CurrentUser.OpenSubKey(_regRoot + @"\Run", writable: true)!)
        {
            run.SetValue("Baru", "app.exe");
        }

        using (var entry = Registry.CurrentUser.CreateSubKey(uninstall + @"\Baru")!)
        {
            entry.SetValue("DisplayName", "Program Baru 1.0");
        }

        var added = InstallTraceRules.Added(before, snapshotter.Take(CancellationToken.None));

        added.Should().BeEquivalentTo(
        [
            new TraceItem(LeftoverKind.Folder, Path.Combine(programFiles, "Baru")),
            new TraceItem(LeftoverKind.RegistryKey, FullKey(@"SOFTWARE\Baru")),
            new TraceItem(LeftoverKind.RegistryKey, FullKey(@"Microsoft\Windows\CurrentVersion\Uninstall\Baru")),
            new TraceItem(LeftoverKind.RegistryValue, FullKey("Run"), "Baru"),
        ]);

        string uninstallKey = InstallTraceRules.UninstallKeysIn(added).Single();
        snapshotter.ReadDisplayName(uninstallKey).Should().Be("Program Baru 1.0");
        added.Should().OnlyContain(i => snapshotter.Exists(i));
        snapshotter.Exists(new TraceItem(LeftoverKind.RegistryValue, FullKey("Run"), "TidakAda")).Should().BeFalse();
        snapshotter.Exists(new TraceItem(LeftoverKind.Folder, Path.Combine(programFiles, "TidakAda"))).Should().BeFalse();
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_regRoot, throwOnMissingSubKey: false);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

public sealed class InstallTraceStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "MyUninstallerTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Saves_reads_and_deletes()
    {
        var store = new JsonInstallTraceStore(_folder, NullLogger<JsonInstallTraceStore>.Instance);
        var trace = new InstallTrace
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Contoh",
            CreatedAt = new DateTimeOffset(2026, 11, 15, 14, 30, 0, TimeSpan.FromHours(7)),
            Items = [new TraceItem(LeftoverKind.RegistryValue, @"HKEY_CURRENT_USER\Run", "Contoh")],
            UninstallKeys = [@"HKEY_CURRENT_USER\X"],
        };

        store.Save(trace);
        File.WriteAllText(Path.Combine(_folder, "rusak.json"), "{ bukan json");

        var read = store.GetAll().Single();
        read.Should().BeEquivalentTo(trace);

        store.Delete(trace.Id);
        store.GetAll().Should().BeEmpty();

        var act = () => store.Delete(@"..\..\Windows\win");
        act.Should().Throw<ArgumentException>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}

[Collection(WpfCollection.Name)]
public class InstallMonitorViewModelTests
{
    private sealed class FakeSnapshotter : ISystemSnapshotter
    {
        public Queue<SystemSnapshot> Next { get; } = new();

        public SystemSnapshot Take(CancellationToken cancellationToken) => Next.Dequeue();

        public string? ReadDisplayName(string uninstallKeyPath) => uninstallKeyPath.EndsWith(@"\Baru") ? "Program Baru 1.0" : null;
    }

    private sealed class MemoryStore : IInstallTraceStore
    {
        public List<InstallTrace> Saved { get; } = [];

        public IReadOnlyList<InstallTrace> GetAll() => Saved;

        public void Save(InstallTrace trace) => Saved.Add(trace);

        public void Delete(string id) => Saved.RemoveAll(t => t.Id == id);
    }

    [StaFact]
    public void Before_install_after_review_save()
    {
        WindowXamlTests.EnsureApplicationResources();
        const string Key = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Baru";
        var snapshotter = new FakeSnapshotter();
        snapshotter.Next.Enqueue(new SystemSnapshot([new TraceItem(LeftoverKind.Folder, @"C:\PF\Lama")]));
        snapshotter.Next.Enqueue(new SystemSnapshot(
        [
            new TraceItem(LeftoverKind.Folder, @"C:\PF\Lama"),
            new TraceItem(LeftoverKind.Folder, @"C:\PF\Baru"),
            new TraceItem(LeftoverKind.RegistryKey, Key),
            new TraceItem(LeftoverKind.Folder, @"C:\Users\u\AppData\Local\CacheLain"),
        ]));
        var store = new MemoryStore();
        string? launched = null;
        using var vm = new InstallMonitorViewModel(snapshotter, store, new FixedClock(new DateTimeOffset(2026, 11, 15, 9, 5, 0, TimeSpan.FromHours(7))),
            pickInstaller: () => @"C:\Unduhan\setup.exe", launch: p => (launched = p) is not null, confirmDelete: _ => true, NullLogger.Instance);

        vm.IsReady.Should().BeTrue();
        vm.StartCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        vm.Phase.Should().Be(MonitorPhase.Installing);

        vm.RunInstallerCommand.Execute(null);
        launched.Should().Be(@"C:\Unduhan\setup.exe");

        vm.FinishCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        vm.Phase.Should().Be(MonitorPhase.Review);
        vm.ProgramName.Should().Be("Program Baru 1.0");
        vm.Items.Should().HaveCount(3);
        vm.StepText.Should().StartWith("Langkah 3 dari 3: 3 benda baru");

        // Folder cache program lain bukan buatan installer: batalkan centangnya.
        vm.Items.Single(i => i.Item.Location.EndsWith("CacheLain")).IsChecked = false;
        vm.SaveCommand.Execute(null);

        var saved = store.Saved.Single();
        saved.Name.Should().Be("Program Baru 1.0");
        saved.Items.Should().HaveCount(2);
        saved.UninstallKeys.Should().Equal(Key);
        vm.Phase.Should().Be(MonitorPhase.Ready);
        vm.Traces.Single().DetailText.Should().Be("Dipantau 15 November 2026 09.05 · 2 benda");
        vm.NoticeText.Should().Be("Jejak \"Program Baru 1.0\" disimpan (2 benda).");

        var window = new InstallMonitorWindow(vm);
        window.Title.Should().Be("Pantau instalasi");
        window.Close();

        vm.SelectedTrace = vm.Traces[0];
        vm.DeleteTraceCommand.Execute(null);
        store.Saved.Should().BeEmpty();
    }

    [StaFact]
    public void Nothing_to_save_without_name_or_items()
    {
        var snapshotter = new FakeSnapshotter();
        snapshotter.Next.Enqueue(new SystemSnapshot([]));
        snapshotter.Next.Enqueue(new SystemSnapshot([new TraceItem(LeftoverKind.Folder, @"C:\PF\X")]));
        using var vm = new InstallMonitorViewModel(snapshotter, new MemoryStore(), new FixedClock(DateTimeOffset.Now),
            () => null, _ => true, _ => true, NullLogger.Instance);

        vm.StartCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        vm.FinishCommand.ExecuteAsync(null).GetAwaiter().GetResult();

        vm.ProgramName.Should().BeEmpty();
        vm.SaveCommand.CanExecute(null).Should().BeFalse("nama program kosong");
        vm.ProgramName = "X";
        vm.SaveCommand.CanExecute(null).Should().BeTrue();
        vm.Items[0].IsChecked = false;
        vm.RefreshCanSave();
        vm.SaveCommand.CanExecute(null).Should().BeFalse("tidak ada yang dicentang");

        vm.CancelCommand.Execute(null);
        vm.Phase.Should().Be(MonitorPhase.Ready);
    }
}

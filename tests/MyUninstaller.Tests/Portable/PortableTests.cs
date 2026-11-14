using Microsoft.Extensions.Logging.Abstractions;
using MyUninstaller.App.Services;
using MyUninstaller.App.ViewModels;
using MyUninstaller.App.Views;
using MyUninstaller.Core.Models;
using MyUninstaller.Core.Portable;
using MyUninstaller.Core.Safety;
using MyUninstaller.Core.Uninstall;
using MyUninstaller.Core.Usage;
using MyUninstaller.Infrastructure.FileSystem;
using MyUninstaller.Infrastructure.Portable;
using MyUninstaller.Infrastructure.Safety;
using MyUninstaller.Tests.Safety;
using MyUninstaller.Tests.Ui;

namespace MyUninstaller.Tests.Portable;

public class PortableRulesTests
{
    private static ExecutableInfo Exe(string path, long length = 100, string? product = null, string? company = null) =>
        new(path, length, product, null, company, null);

    private static InstalledProgram Installed(string name, string? location = null, string? publisher = null,
        string? uninstall = null, string? icon = null) => new()
    {
        Id = name,
        DisplayName = name,
        Source = ProgramSource.Registry64,
        Publisher = publisher,
        InstallLocation = location,
        UninstallString = uninstall,
        IconPath = icon,
    };

    [Theory]
    [InlineData("setup.exe", true)]
    [InlineData("unins000.exe", true)]
    [InlineData("Update.exe", true)]
    [InlineData("CrashReporter.exe", true)]
    [InlineData("nestopia.exe", false)]
    [InlineData("Ryujinx.exe", false)]
    public void Helper_executables(string name, bool helper) =>
        PortableRules.IsHelperExecutable(name).Should().Be(helper);

    [Fact]
    public void Main_executable_prefers_folder_name_then_size()
    {
        var exes = new[] { Exe(@"C:\T\Nestopia\big.exe", 9000), Exe(@"C:\T\Nestopia\nestopia.exe", 10), Exe(@"C:\T\Nestopia\setup.exe", 99999) };
        PortableRules.ChooseMainExecutable("Nestopia", exes)!.Path.Should().EndWith("nestopia.exe");
        PortableRules.ChooseMainExecutable("Lain", exes)!.Path.Should().EndWith("big.exe");
        PortableRules.ChooseMainExecutable("X", [Exe(@"C:\X\setup.exe")]).Should().BeNull();
    }

    [Fact]
    public void Display_name_falls_back_to_folder() =>
        PortableRules.DisplayName("ffmpeg", Exe(@"C:\ffmpeg\ffmpeg.exe")).Should().Be("ffmpeg");

    [Theory]
    [InlineData("DOWNLOAD", new string[0], true)]
    [InlineData("Unduhan Saya", new string[0], true)]
    [InlineData("Compressed", new string[0], true)]
    [InlineData("Kumpulan", new[] { "a.zip", "b.rar", "c.pdf", "d.mp4" }, true)]
    [InlineData("Nestopia", new[] { "nestopia.exe", "readme.pdf", "icon.png" }, false)]
    public void Storage_folders(string name, string[] files, bool storage) =>
        PortableRules.IsStorageFolder(name, files).Should().Be(storage);

    [Theory]
    [InlineData(".git")]
    [InlineData("Aplikasi.sln")]
    [InlineData("package.json")]
    [InlineData("Web.csproj")]
    public void Project_markers(string entry) =>
        PortableRules.IsProjectFolder(["README.md", entry]).Should().BeTrue();

    [Fact]
    public void Installed_program_folders_and_their_parents_are_owned()
    {
        var owned = PortableRules.OwnedFolders(
        [
            Installed("A", location: @"D:\Games\A\"),
            Installed("B", uninstall: "\"E:\\Apps\\B\\unins000.exe\" /x"),
            Installed("C", icon: @"F:\C\c.exe,0"),
            Installed("D", uninstall: "MsiExec.exe /X{1234}"),
        ]);

        PortableRules.IsOwned(@"D:\Games\A", owned).Should().BeTrue();
        PortableRules.IsOwned(@"D:\Games\A\bin", owned).Should().BeTrue();
        PortableRules.IsOwned(@"D:\Games", owned).Should().BeTrue("folder yang memuat program terpasang bukan program portable");
        PortableRules.IsInsideOwned(@"D:\Games", owned).Should().BeFalse("isinya tetap ditelusuri");
        PortableRules.IsOwned(@"E:\Apps\B", owned).Should().BeTrue();
        PortableRules.IsOwned(@"F:\C", owned).Should().BeTrue();
        PortableRules.IsOwned(@"D:\Games\Z", owned).Should().BeFalse();
    }

    [Fact]
    public void Registered_program_without_install_location_is_recognized_by_name()
    {
        // Python dari installer Burn: entri Uninstall hanya menunjuk ke Package Cache.
        var python = Installed("Python 3.12.7 (64-bit)", publisher: "Python Software Foundation",
            uninstall: @"C:\Users\u\AppData\Local\Package Cache\{x}\python-3.12.7-amd64.exe /uninstall");

        PortableRules.MatchesInstalledProgram("Python312", Exe(@"C:\P\Python312\python.exe", product: "Python", company: "Python Software Foundation"), [python])
            .Should().BeTrue();
        PortableRules.MatchesInstalledProgram("Nestopia", Exe(@"E:\G\Nestopia\nestopia.exe"), [python]).Should().BeFalse();
    }
}

/// <summary>Pemindai pada pohon folder sementara.</summary>
[Trait("Category", "Integration")]
public sealed class PortableScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MyUninstallerTests", Guid.NewGuid().ToString("N"));

    private sealed class NoUsage : IUsageHistory
    {
        public UsageIndex Load() => UsageIndex.Empty;

        public IReadOnlyList<string> ExecutablesIn(string installLocation) => [];
    }

    private string File(string relative, int bytes = 10)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void Finds_only_real_portable_folders()
    {
        File(@"Tools\Nestopia\nestopia.exe");
        File(@"Tools\Nestopia\data.dll");
        File(@"Tools\ffmpeg\bin\ffmpeg.exe");
        File(@"Kumpulan\Optimizer.exe");
        foreach (string f in new[] { "a.zip", "b.rar", "c.7z", "d.pdf" })
        {
            File(@"Kumpulan\" + f);
        }

        File(@"Unduhan Saya\Tabula\tabula.exe");
        File(@"Proyek\Web.sln");
        File(@"Proyek\src\bin\Debug\web.exe");
        File(@"Terpasang\App\app.exe");
        File(@"Python\Python312\python.exe");
        File(@"Hanya\setup.exe");
        File(@"lepas.exe");
        File(@"Dalam\1\2\3\4\terlalu-dalam.exe");

        InstalledProgram[] installed =
        [
            new() { Id = "a", DisplayName = "App", Source = ProgramSource.Registry64, InstallLocation = Path.Combine(_root, @"Terpasang\App") },
            new() { Id = "p", DisplayName = "Python 3.12.7 (64-bit)", Source = ProgramSource.Registry64 },
        ];

        var scanner = new PortableProgramScanner(new FolderSizeCalculator(), new NoUsage(), new ProtectedPaths([], []),
            NullLogger<PortableProgramScanner>.Instance, shortcutFolders: [], defaultRoots: [_root]);

        var found = scanner.Scan(scanner.DefaultRoots, installed, CancellationToken.None);

        found.Select(p => Path.GetRelativePath(_root, p.Folder)).Should().BeEquivalentTo(
            @"Tools\Nestopia", @"Tools\ffmpeg", @"Unduhan Saya\Tabula");
        found.Single(p => p.Folder.EndsWith("Nestopia")).SizeBytes.Should().Be(20);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

public sealed class PortableServiceTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "MyUninstallerTests", Guid.NewGuid().ToString("N"));

    private sealed class Processes(params RunningProcess[] running) : IProcessInspector
    {
        public IReadOnlyList<RunningProcess> GetRunningProcesses() => running;

        public bool TryKill(RunningProcess process) => false;
    }

    private (PortableProgramService Service, SqliteSessionJournal Journal) Create(params RunningProcess[] running)
    {
        var protectedPaths = new ProtectedPaths([], []);
        var journal = new SqliteSessionJournal(Path.Combine(_work, "riwayat.db"), new FixedClock(DateTimeOffset.Now));
        var quarantine = new QuarantineService(Path.Combine(_work, "Karantina"), protectedPaths, NullLogger<QuarantineService>.Instance);
        var registry = new RegistryBackupService(Path.Combine(_work, "Karantina"), NullLogger<RegistryBackupService>.Instance);
        return (new PortableProgramService(new Processes(running), new SafeRemover(protectedPaths, quarantine, registry, journal), journal,
            NullLogger<PortableProgramService>.Instance), journal);
    }

    private PortableProgram Program()
    {
        string folder = Path.Combine(_work, "Data", "Nestopia");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "nestopia.exe"), "exe");
        string shortcut = Path.Combine(_work, "Data", "Desktop", "Nestopia.lnk");
        Directory.CreateDirectory(Path.GetDirectoryName(shortcut)!);
        File.WriteAllText(shortcut, "lnk");
        return new PortableProgram
        {
            Name = "Nestopia",
            Folder = folder,
            MainExecutable = Path.Combine(folder, "nestopia.exe"),
            Shortcuts = [shortcut],
        };
    }

    [Fact]
    public void Running_program_is_not_removed()
    {
        var program = Program();
        var (service, _) = Create(new RunningProcess(5, "nestopia", program.MainExecutable));

        var result = service.Remove(program, _ => throw new InvalidOperationException("tidak boleh ditanya"));

        result.Removed.Should().BeFalse();
        result.Message.Should().Be("\"Nestopia\" sedang berjalan (nestopia). Tutup programnya dulu, lalu coba lagi.");
        Directory.Exists(program.Folder).Should().BeTrue();
    }

    [Fact]
    public void Folder_and_shortcuts_go_to_quarantine()
    {
        var program = Program();
        var (service, journal) = Create(new RunningProcess(5, "lain", @"C:\Lain\NestopiaX\lain.exe"));

        service.Remove(program, _ => false).Removed.Should().BeFalse();
        Directory.Exists(program.Folder).Should().BeTrue("pengguna membatalkan");

        var result = service.Remove(program, _ => true);

        result.Removed.Should().BeTrue();
        Directory.Exists(program.Folder).Should().BeFalse();
        File.Exists(program.Shortcuts[0]).Should().BeFalse();
        var session = journal.GetSessions().Single();
        session.Mode.Should().Be("Portable");
        journal.GetActions(session.Id).Should().HaveCount(2).And.OnlyContain(a => a.Kind == JournalActionKind.QuarantinePath);
    }

    [StaFact]
    public void Window_and_view_model_load()
    {
        WindowXamlTests.EnsureApplicationResources();
        var program = Program() with { SizeBytes = 2_048, LastUsed = new DateTimeOffset(2026, 1, 2, 10, 0, 0, TimeSpan.FromHours(7)) };
        var (service, _) = Create();
        var vm = new PortableProgramsViewModel(new FixedScanner(program), service, new NullShell(), () => [], _ => false, () => null,
            new DateOnly(2026, 11, 15), NullLogger.Instance);

        // Tanpa await: kelanjutan tidak boleh pindah dari utas STA sebelum jendela dibuat.
        vm.ScanCommand.ExecuteAsync(null).GetAwaiter().GetResult();

        vm.SummaryText.Should().Be("1 program portable, total 2 KB");
        vm.Items.Single().IsRarelyUsed.Should().BeTrue();
        vm.Items.Single().DetailText.Should().Be("2 KB · terakhir dipakai 2 Januari 2026 · 1 pintasan");

        var window = new PortableProgramsWindow(vm);
        window.Title.Should().Be("Program portable");
        window.Close();
    }

    private sealed class FixedScanner(params PortableProgram[] programs) : IPortableProgramScanner
    {
        public IReadOnlyList<string> DefaultRoots => [@"C:\"];

        public IReadOnlyList<PortableProgram> Scan(IReadOnlyList<string> roots, IReadOnlyList<InstalledProgram> installed, CancellationToken cancellationToken) => programs;
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

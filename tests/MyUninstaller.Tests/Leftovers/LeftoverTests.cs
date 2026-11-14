using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using MyUninstaller.Core.Leftovers;
using MyUninstaller.Core.Models;
using MyUninstaller.Core.Safety;
using MyUninstaller.Core.Scanning;
using MyUninstaller.Infrastructure.FileSystem;
using MyUninstaller.Infrastructure.Leftovers;
using MyUninstaller.Infrastructure.Scanning;
using Xunit.Abstractions;

namespace MyUninstaller.Tests.Leftovers;

public class NameMatcherTests
{
    [Theory]
    [InlineData("7-Zip 24.09 (x64)", "7zip")]
    [InlineData("Notepad++ (64-bit x64)", "notepad")]
    [InlineData("VLC media player", "vlcmediaplayer")]
    [InlineData("Python 3.13.1 (64-bit)", "python")]
    [InlineData("Zoom Workplace v6", "zoomworkplace")]
    [InlineData("Café", "cafe")]
    [InlineData("Ünicode Spiel", "unicodespiel")]
    [InlineData("", "")]
    public void Normalize(string input, string expected) => NameMatcher.Normalize(input).Should().Be(expected);

    [Theory]
    [InlineData("Google LLC", "google")]
    [InlineData("Igor Pavlov", "igorpavlov")]
    [InlineData("Zoom Video Communications, Inc.", "zoomvideocommunications")]
    [InlineData("Microsoft Corporation", "")]
    [InlineData(null, "")]
    public void NormalizePublisher(string? input, string expected) => NameMatcher.NormalizePublisher(input).Should().Be(expected);

    [Theory]
    [InlineData("7-Zip", NameMatch.Exact)]
    [InlineData("7zip", NameMatch.Exact)]
    [InlineData("VideoLAN", NameMatch.None)]
    [InlineData("vlc", NameMatch.Exact)]
    [InlineData("VLC-Plugins-Cache", NameMatch.None)]
    [InlineData("Common", NameMatch.None)]
    [InlineData("Update", NameMatch.None)]
    [InlineData("Data", NameMatch.None)]
    public void Compare(string candidate, NameMatch expected)
    {
        string[] names = ["7zip", "vlc"];
        NameMatcher.Compare(candidate, names).Should().Be(expected);
    }

    [Fact]
    public void Similar_only_when_candidate_contains_program_name()
    {
        NameMatcher.Compare("Audacity-Backup", ["audacity"]).Should().Be(NameMatch.Similar);
        NameMatcher.Compare("Aud", ["audacity"]).Should().Be(NameMatch.None);

        // Kasus nyata: folder bersama yang lebih pendek dari nama program bukan "mirip".
        NameMatcher.Compare("epson", ["epsonscan2"]).Should().Be(NameMatch.None);
        NameMatcher.Compare("Android", ["androidstudio"]).Should().Be(NameMatch.None);
    }

    [Fact]
    public void Glued_version_is_removed() =>
        NameMatcher.Normalize("AndroidStudio2025.3.1").Should().Be("androidstudio");

    [Fact]
    public void Program_names_use_install_folder_and_name_without_publisher_but_never_first_word()
    {
        var firefox = new InstalledProgram
        {
            Id = "x",
            DisplayName = "Mozilla Firefox (x64 id)",
            Publisher = "Mozilla",
            Source = ProgramSource.Registry64,
            InstallLocation = @"C:\Program Files\Mozilla Firefox",
        };
        var studio = new InstalledProgram { Id = "y", DisplayName = "Android Studio", Publisher = "Google LLC", Source = ProgramSource.Registry64 };
        var desktop = new InstalledProgram { Id = "z", DisplayName = "GitHub Desktop", Publisher = "GitHub, Inc.", Source = ProgramSource.Registry64 };

        NameMatcher.ProgramNames(firefox).Should().BeEquivalentTo(["mozillafirefoxid", "firefoxid", "mozillafirefox"]);

        // Kasus nyata: "Android" = folder SDK, "GitHub" = folder repositori pengguna.
        NameMatcher.ProgramNames(studio).Should().NotContain("android");
        NameMatcher.ProgramNames(desktop).Should().Equal("githubdesktop");
    }
}

public class ConfidenceScorerTests
{
    [Theory]
    [InlineData(LeftoverEvidence.InstallLocation, LeftoverKind.Folder, LeftoverGroup.Safe)]
    [InlineData(LeftoverEvidence.UninstallKey, LeftoverKind.RegistryKey, LeftoverGroup.Safe)]
    [InlineData(LeftoverEvidence.PointsIntoInstallLocation, LeftoverKind.Shortcut, LeftoverGroup.Safe)]
    [InlineData(LeftoverEvidence.ExactNameUnderPublisher, LeftoverKind.Folder, LeftoverGroup.Safe)]
    [InlineData(LeftoverEvidence.ExactName, LeftoverKind.RegistryKey, LeftoverGroup.Safe)]
    [InlineData(LeftoverEvidence.PublisherOnly, LeftoverKind.Folder, LeftoverGroup.Maybe)]
    [InlineData(LeftoverEvidence.ExactNameElsewhere, LeftoverKind.Folder, LeftoverGroup.Maybe)]
    [InlineData(LeftoverEvidence.SimilarName, LeftoverKind.Folder, LeftoverGroup.Doubtful)]
    public void Groups_match_prd(LeftoverEvidence evidence, LeftoverKind kind, LeftoverGroup group) =>
        ConfidenceScorer.GroupOf(ConfidenceScorer.Score(evidence, kind)).Should().Be(group);

    [Fact]
    public void Services_and_tasks_are_capped() =>
        ConfidenceScorer.Score(LeftoverEvidence.PointsIntoInstallLocation, LeftoverKind.Service).Should().Be(85);

    [Theory]
    [InlineData(100, true)]
    [InlineData(80, true)]
    [InlineData(79, false)]
    [InlineData(50, false)]
    [InlineData(10, false)]
    public void Only_safe_group_is_checked_by_default(int score, bool expected) =>
        ConfidenceScorer.IsCheckedByDefault(score).Should().Be(expected);
}

public class LeftoverContextTests
{
    private static InstalledProgram P(string name, string? publisher = null, string? location = null) => new()
    {
        Id = name,
        DisplayName = name,
        Publisher = publisher,
        Source = ProgramSource.Registry64,
        InstallLocation = location,
    };

    private static readonly InstalledProgram Vlc = P("VLC media player", "VideoLAN", @"C:\Program Files\VideoLAN\VLC");

    [Fact]
    public void Other_programs_from_same_publisher_protect_publisher_folder()
    {
        var chrome = P("Google Chrome", "Google LLC", @"C:\Program Files\Google\Chrome\Application");
        var drive = P("Google Drive", "Google LLC", @"C:\Program Files\Google\Drive File Stream");

        var ctx = new LeftoverContext(chrome, [drive]);

        ctx.PublisherShared.Should().BeTrue();
        ctx.EvidenceForName("Google", underPublisher: false).Should().BeNull();
        ctx.BelongsToOtherProgram(@"C:\Program Files\Google").Should().BeTrue("folder itu membungkus Google Drive");
        ctx.BelongsToOtherProgram(@"C:\Program Files\Google\Drive File Stream\x").Should().BeTrue();
        ctx.BelongsToOtherProgram(@"C:\Program Files\Google\Chrome").Should().BeFalse();
    }

    [Fact]
    public void Publisher_only_when_publisher_not_shared()
    {
        var ctx = new LeftoverContext(Vlc, []);
        ctx.EvidenceForName("VideoLAN", underPublisher: false).Should().Be(LeftoverEvidence.PublisherOnly);
        ctx.EvidenceForName("VLC", underPublisher: true).Should().Be(LeftoverEvidence.ExactNameUnderPublisher);
    }

    [Fact]
    public void Name_shared_with_installed_program_is_ambiguous()
    {
        var py312 = P("Python 3.12.4 (64-bit)", "Python Software Foundation");
        var py313 = P("Python 3.13.1 (64-bit)", "Python Software Foundation");

        new LeftoverContext(py313, [py312]).EvidenceForName("Python", false).Should().BeNull();
        new LeftoverContext(py313, []).EvidenceForName("Python", false).Should().Be(LeftoverEvidence.ExactName);
    }

    [Theory]
    [InlineData(@"""C:\Program Files\VideoLAN\VLC\vlc.exe"" --started-from-file", true)]
    [InlineData(@"C:\Program Files\VideoLAN\VLC\vlc.exe", true)]
    [InlineData(@"rundll32.exe ""C:\Program Files\VideoLAN\VLC\x.dll"",Run", true)]
    [InlineData(@"C:\Program Files\VideoLAN\VLC2\vlc.exe", false)]
    [InlineData(@"C:\Windows\System32\cmd.exe", false)]
    [InlineData(null, false)]
    public void Command_points_into_install_location(string? command, bool expected) =>
        new LeftoverContext(Vlc, []).CommandPointsIntoInstallLocation(command).Should().Be(expected);

    [Fact]
    public void Nested_program_inside_install_folder_is_not_claimed()
    {
        var suite = P("Suite", null, @"D:\Apps\Suite");
        var plugin = P("Suite Plugin", null, @"D:\Apps\Suite\Plugin");

        var ctx = new LeftoverContext(suite, [plugin]);

        ctx.IsInsideInstallLocation(@"D:\Apps\Suite\suite.exe").Should().BeTrue();
        ctx.IsInsideInstallLocation(@"D:\Apps\Suite\Plugin\plugin.exe").Should().BeFalse();
    }

    [Fact]
    public void Install_location_is_inferred_from_uninstaller()
    {
        var program = P("App") with { UninstallString = @"""D:\Apps\App\unins000.exe"" /SILENT" };

        InstallLocationInference.WithInferredLocation(program, new InstallLocationRules([]))
            .InstallLocation.Should().Be(@"D:\Apps\App");
    }

    [Fact]
    public void Generic_uninstaller_subfolder_climbs_to_program_folder()
    {
        var program = P("Epson Scan 2") with { UninstallString = @"""D:\Apps\epson\Epson Scan 2\Setup\setup.exe"" /remove" };

        InstallLocationInference.WithInferredLocation(program, new InstallLocationRules([]))
            .InstallLocation.Should().Be(@"D:\Apps\epson\Epson Scan 2");
    }

    [Fact]
    public void Installer_cache_is_not_inferred_as_install_location()
    {
        var program = P("App") with { UninstallString = @"""C:\ProgramData\Package Cache\{X}\setup.exe"" /uninstall" };

        InstallLocationInference.WithInferredLocation(program, new InstallLocationRules([]))
            .InstallLocation.Should().BeNull();
    }
}

/// <summary>
/// Pemindai sisa pada lingkungan buatan: folder sementara sebagai "Program Files/AppData/Start Menu"
/// dan kunci HKCU\Software\MyUninstallerTests\&lt;guid&gt; sebagai "SOFTWARE" dan "Run".
/// </summary>
[Trait("Category", "Integration")]
public sealed class LeftoverScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MyUninstallerTests", Guid.NewGuid().ToString("N"));
    private readonly string _regRoot = @"Software\MyUninstallerTests\" + Guid.NewGuid().ToString("N");

    private string ProgramFiles => Path.Combine(_root, "Program Files");

    private string AppData => Path.Combine(_root, "AppData");

    private string StartMenu => Path.Combine(_root, "Start Menu");

    private string Documents => Path.Combine(_root, "Dokumen");

    private LeftoverScanner CreateScanner()
    {
        var roots = new LeftoverScanRoots
        {
            FolderRoots = [ProgramFiles, AppData, StartMenu],
            UserDataRoots = [Documents],
            ShortcutRoots = [StartMenu],
            SoftwareRoots = [new RegistryRoot(RegistryHive.CurrentUser, _regRoot + @"\SOFTWARE")],
            RunKeys = [new RegistryRoot(RegistryHive.CurrentUser, _regRoot + @"\Run")],
            AppPathsKeys = [],
            ClsidKeys = [],
            DriveRoots = [],
        };

        return new LeftoverScanner(
            roots,
            new ProtectedPaths([], [ProgramFiles, AppData, StartMenu, Documents]),
            new InstallLocationRules([ProgramFiles]),
            new FolderSizeCalculator(),
            NullLogger<LeftoverScanner>.Instance);
    }

    private static void Write(string path, string content = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public async Task Finds_leftovers_scores_them_and_spares_other_programs()
    {
        // Program yang dihapus: "Kalkulator Hebat" dari penerbit "Contoh Soft".
        string install = Path.Combine(ProgramFiles, "Contoh Soft", "Kalkulator Hebat");
        Write(Path.Combine(install, "sisa.log"), "0123456789");
        Write(Path.Combine(AppData, "Kalkulator Hebat", "config.ini"));
        Write(Path.Combine(AppData, "Contoh Soft", "Kalkulator Hebat", "cache.bin"));
        Write(Path.Combine(AppData, "KalkulatorHebatBackup", "x.txt")); // mirip → Ragu
        Write(Path.Combine(Documents, "Kalkulator Hebat", "karya-saya.txt")); // data pengguna → Ragu
        Write(Path.Combine(Documents, "KalkulatorHebatProyek", "x.txt")); // mirip di data pengguna → tidak dilaporkan

        // Program lain dari penerbit sama yang MASIH terpasang.
        string otherInstall = Path.Combine(ProgramFiles, "Contoh Soft", "Pena Ajaib");
        Write(Path.Combine(otherInstall, "pena.exe"));
        Write(Path.Combine(AppData, "Pena Ajaib", "x.ini"));
        Write(Path.Combine(AppData, "Common", "x.ini")); // nama umum, tidak pernah cocok

        // Pintasan ke exe di folder instalasi.
        string shortcut = Path.Combine(StartMenu, "Kalkulator.lnk");
        CreateShortcut(shortcut, Path.Combine(install, "kalkulator.exe"));

        using (var software = Registry.CurrentUser.CreateSubKey(_regRoot + @"\SOFTWARE"))
        {
            software.CreateSubKey(@"Contoh Soft\Kalkulator Hebat")!.Dispose();
            software.CreateSubKey(@"Contoh Soft\Pena Ajaib")!.Dispose();
            software.CreateSubKey("KalkulatorHebat")!.Dispose();
        }

        using (var run = Registry.CurrentUser.CreateSubKey(_regRoot + @"\Run"))
        {
            run.SetValue("KalkulatorTray", $"\"{Path.Combine(install, "tray.exe")}\" /min");
            run.SetValue("PenaAjaib", $"\"{Path.Combine(otherInstall, "pena.exe")}\"");
        }

        var target = new InstalledProgram
        {
            Id = "Registry64:Kalkulator",
            DisplayName = "Kalkulator Hebat 2.1 (x64)",
            Publisher = "Contoh Soft Inc.",
            Source = ProgramSource.Registry64,
            InstallLocation = install,
        };
        var other = new InstalledProgram
        {
            Id = "Registry64:Pena",
            DisplayName = "Pena Ajaib",
            Publisher = "Contoh Soft",
            Source = ProgramSource.Registry64,
            InstallLocation = otherInstall,
        };

        var sw = Stopwatch.StartNew();
        var items = await CreateScanner().ScanAsync(target, [other], deep: false);
        sw.ElapsedMilliseconds.Should().BeLessThan(10_000, "PRD: mode Normal di bawah 10 detik");

        string regBase = @"HKEY_CURRENT_USER\" + _regRoot + @"\SOFTWARE";
        var byLocation = items.ToDictionary(i => i.ValueName is null ? i.Location : i.Location + "|" + i.ValueName, i => i.Group);

        byLocation.Should().Contain(install, LeftoverGroup.Safe);
        byLocation.Should().Contain(Path.Combine(AppData, "Kalkulator Hebat"), LeftoverGroup.Safe);
        byLocation.Should().Contain(Path.Combine(AppData, "Contoh Soft", "Kalkulator Hebat"), LeftoverGroup.Safe);
        byLocation.Should().Contain(Path.Combine(AppData, "KalkulatorHebatBackup"), LeftoverGroup.Doubtful);
        byLocation.Should().Contain(Path.Combine(Documents, "Kalkulator Hebat"), LeftoverGroup.Doubtful);
        byLocation.Should().NotContainKey(Path.Combine(Documents, "KalkulatorHebatProyek"));
        byLocation.Should().Contain(shortcut, LeftoverGroup.Safe);
        byLocation.Should().Contain($@"{regBase}\Contoh Soft\Kalkulator Hebat", LeftoverGroup.Safe);
        byLocation.Should().Contain($@"{regBase}\KalkulatorHebat", LeftoverGroup.Safe);
        byLocation.Should().Contain($@"HKEY_CURRENT_USER\{_regRoot}\Run|KalkulatorTray", LeftoverGroup.Safe);

        // Tidak pernah: milik program lain, folder/kunci penerbit bersama, nama umum.
        items.Should().NotContain(i => i.Location.Contains("Pena Ajaib", StringComparison.OrdinalIgnoreCase));
        items.Should().NotContain(i => i.ValueName == "PenaAjaib");
        items.Should().NotContain(i => i.Location == Path.Combine(ProgramFiles, "Contoh Soft"));
        items.Should().NotContain(i => i.Location == Path.Combine(AppData, "Contoh Soft"));
        items.Should().NotContain(i => i.Location == $@"{regBase}\Contoh Soft");
        items.Should().NotContain(i => i.Location.EndsWith("Common", StringComparison.OrdinalIgnoreCase));

        items.Single(i => i.Location == install).SizeBytes.Should().Be(10);
    }

    [Fact]
    public async Task Publisher_folder_is_offered_when_no_other_program_uses_it()
    {
        Write(Path.Combine(AppData, "Contoh Soft", "Kalkulator Hebat", "x.ini"));
        var target = new InstalledProgram
        {
            Id = "x",
            DisplayName = "Kalkulator Hebat",
            Publisher = "Contoh Soft",
            Source = ProgramSource.Registry64,
        };

        var items = await CreateScanner().ScanAsync(target, [], deep: false);

        items.Should().Contain(i => i.Location == Path.Combine(AppData, "Contoh Soft") && i.Group == LeftoverGroup.Maybe);
    }

    private static void CreateShortcut(string path, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string script = $"$s=(New-Object -ComObject WScript.Shell).CreateShortcut('{path}'); $s.TargetPath='{target}'; $s.Save()";
        using var p = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command \"{script}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit(30_000);
        ShortcutResolver.GetTarget(path).Should().Be(target);
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_regRoot, throwOnMissingSubKey: false);
        using (var parent = Registry.CurrentUser.OpenSubKey(@"Software\MyUninstallerTests", writable: true))
        {
            if (parent is { SubKeyCount: 0, ValueCount: 0 })
            {
                Registry.CurrentUser.DeleteSubKey(@"Software\MyUninstallerTests", throwOnMissingSubKey: false);
            }
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

/// <summary>
/// Uji baca-saja pada sistem sungguhan: berpura-pura program yang masih terpasang sudah dihapus, lalu
/// memastikan hasil pindai tidak pernah menyentuh daftar lindung dan cukup cepat. Tidak menghapus apa pun.
/// </summary>
[Trait("Category", "Integration")]
public class LeftoverScannerRealSystemTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_system_scan_is_safe_and_fast(bool deep)
    {
        var programs = await new ProgramScanner(
            new MyUninstaller.Infrastructure.WindowsRegistry.RegistryUninstallReader(NullLogger<MyUninstaller.Infrastructure.WindowsRegistry.RegistryUninstallReader>.Instance),
            new MyUninstaller.Infrastructure.Store.StorePackageReader(NullLogger<MyUninstaller.Infrastructure.Store.StorePackageReader>.Instance),
            NullLogger<ProgramScanner>.Instance).ScanAsync();

        var target = programs.Where(p => !p.IsSystemComponent && p.Source != ProgramSource.Store && p.InstallLocation is not null)
            .OrderBy(p => p.DisplayName.Contains("Git", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .First();
        var protectedPaths = ProtectedPaths.ForCurrentSystem(@"C:\ProgramData\MyUninstaller");
        var scanner = new LeftoverScanner(protectedPaths, InstallLocationRules.ForCurrentSystem(), new FolderSizeCalculator(), NullLogger<LeftoverScanner>.Instance);

        var sw = Stopwatch.StartNew();
        var items = await scanner.ScanAsync(target, [.. programs.Where(p => p.Id != target.Id)], deep);
        sw.Stop();

        output.WriteLine($"{target.DisplayName} ({(deep ? "Super" : "Normal")}): {items.Count} temuan dalam {sw.ElapsedMilliseconds} ms");
        foreach (var item in items)
        {
            output.WriteLine($"  [{item.Score,3} {item.Group,-9}] {item.Kind,-13} {item.Location}{(item.ValueName is null ? "" : " → " + item.ValueName)}");
        }

        items.Where(i => i.Kind is LeftoverKind.Folder or LeftoverKind.File or LeftoverKind.Shortcut)
            .Should().OnlyContain(i => !protectedPaths.IsProtected(i.Location));
        items.Where(i => i.Kind == LeftoverKind.RegistryKey).Should().OnlyContain(i => !ProtectedRegistryKeys.IsProtected(i.Location));
        items.Should().NotContain(i => Path.GetFileName(i.Location).StartsWith('.') && i.Evidence == LeftoverEvidence.ExactNameElsewhere,
            "folder bertitik (mis. .git repositori) bukan sisa program");

        // PRD: Normal < 10 detik (terukur ±1 detik). Kelonggaran untuk tes lain yang berjalan paralel.
        if (!deep)
        {
            sw.ElapsedMilliseconds.Should().BeLessThan(20_000);
        }
    }
}

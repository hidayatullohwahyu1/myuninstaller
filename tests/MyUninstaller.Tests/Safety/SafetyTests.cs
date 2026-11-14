using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using MyUninstaller.Core.Abstractions;
using MyUninstaller.Core.Models;
using MyUninstaller.Core.Safety;
using MyUninstaller.Core.Scanning;
using MyUninstaller.Core.Uninstall;
using MyUninstaller.Infrastructure.Safety;

namespace MyUninstaller.Tests.Safety;

internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset Now { get; set; } = now;
}

public class ProtectedPathsTests
{
    private readonly ProtectedPaths _rules = new(
        forbiddenTrees: [@"C:\Windows", @"C:\Program Files\Common Files", @"C:\ProgramData\Microsoft", @"C:\ProgramData\MyUninstaller"],
        containers: [@"C:\Program Files", @"C:\ProgramData", @"C:\Users\Budi\AppData\Roaming"]);

    [Theory]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts")]
    [InlineData(@"c:\windows\winsxs")]
    [InlineData(@"C:\Program Files\Common Files\Vendor")]
    [InlineData(@"C:\ProgramData\Microsoft\Windows\Start Menu")]
    [InlineData(@"C:\ProgramData\MyUninstaller\Karantina")]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Program Files\")]
    [InlineData(@"C:\ProgramData")]
    [InlineData(@"C:\Users\Budi\AppData\Roaming")]
    [InlineData(@"C:\")]
    [InlineData(@"D:\")]
    [InlineData(@"\\server\share\app")]
    [InlineData(@"relatif\path")]
    [InlineData("")]
    [InlineData(null)]
    public void Protected(string? path) => _rules.IsProtected(path).Should().BeTrue();

    [Theory]
    [InlineData(@"C:\Program Files\7-Zip")]
    [InlineData(@"C:\Program Files\Common Files Extra\App")] // bukan Common Files
    [InlineData(@"C:\ProgramData\Vendor\App")]
    [InlineData(@"C:\Users\Budi\AppData\Roaming\Vendor")]
    [InlineData(@"D:\Games\Steam")]
    [InlineData(@"C:\WindowsApps2\x")]
    public void Not_protected(string path) => _rules.IsProtected(path).Should().BeFalse();

    [Fact]
    public void Path_traversal_is_normalized_before_checking() =>
        _rules.IsProtected(@"C:\Program Files\7-Zip\..\..\Windows\System32").Should().BeTrue();

    [Fact]
    public void Current_system_protects_key_locations()
    {
        var rules = ProtectedPaths.ForCurrentSystem(@"C:\ProgramData\MyUninstaller");
        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);

        rules.IsProtected(F(Environment.SpecialFolder.Windows)).Should().BeTrue();
        rules.IsProtected(F(Environment.SpecialFolder.System)).Should().BeTrue();
        rules.IsProtected(F(Environment.SpecialFolder.ProgramFiles)).Should().BeTrue();
        rules.IsProtected(F(Environment.SpecialFolder.CommonProgramFiles)).Should().BeTrue();
        rules.IsProtected(Path.Combine(F(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows")).Should().BeTrue();
        rules.IsProtected(F(Environment.SpecialFolder.UserProfile)).Should().BeTrue();
        rules.IsProtected(F(Environment.SpecialFolder.DesktopDirectory)).Should().BeTrue();
        rules.IsProtected(Path.Combine(F(Environment.SpecialFolder.ProgramFiles), "WindowsApps", "x")).Should().BeTrue();
        rules.IsProtected(@"C:\ProgramData\MyUninstaller\Karantina\x").Should().BeTrue();

        rules.IsProtected(Path.Combine(F(Environment.SpecialFolder.ProgramFiles), "7-Zip")).Should().BeFalse();
        rules.IsProtected(Path.Combine(F(Environment.SpecialFolder.ApplicationData), "Vendor", "App")).Should().BeFalse();
    }
}

public class ProtectedRegistryKeysTests
{
    [Theory]
    [InlineData(@"HKEY_LOCAL_MACHINE")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE")]
    [InlineData(@"HKLM\SOFTWARE\WOW6432Node")]
    [InlineData(@"HKEY_CURRENT_USER\Software")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\VendorSvc")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Run")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\App\Sub")]
    [InlineData(@"HKEY_CURRENT_USER\Software\Classes\CLSID")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\CLSID\{X}\InprocServer32")]
    [InlineData(@"HKEY_CURRENT_USER\Software\Classes\.txt")]
    [InlineData(@"HKEY_CLASSES_ROOT\.txt")]
    [InlineData(@"HKEY_USERS\.DEFAULT\Software\Vendor")]
    [InlineData("")]
    [InlineData(null)]
    public void Protected(string? key) => ProtectedRegistryKeys.IsProtected(key).Should().BeTrue();

    [Theory]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\7-Zip")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{GUID}")]
    [InlineData(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall\Discord")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\app.exe")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\VideoLAN\VLC")]
    [InlineData(@"HKEY_CURRENT_USER\Software\Classes\CLSID\{11111111-2222-3333-4444-555555555555}")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Classes\CLSID\{11111111-2222-3333-4444-555555555555}")]
    [InlineData(@"HKCU\Software\Vendor\App")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Vendor")]
    public void Not_protected(string key) => ProtectedRegistryKeys.IsProtected(key).Should().BeFalse();

    [Theory]
    [InlineData(@"HKLM\SOFTWARE\WOW6432Node\Vendor\", @"HKEY_LOCAL_MACHINE\SOFTWARE\Vendor")]
    [InlineData(@"HKCU\\Software\\Vendor", @"HKEY_CURRENT_USER\Software\Vendor")]
    public void Normalize(string input, string expected) =>
        ProtectedRegistryKeys.Normalize(input).Should().Be(expected);
}

public sealed class JournalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MyUninstallerTests", Guid.NewGuid().ToString("N"));
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 11, 14, 14, 30, 0, TimeSpan.FromHours(7)));

    private SqliteSessionJournal Create() => new(Path.Combine(_dir, "riwayat.db"), _clock);

    [Fact]
    public void Records_session_and_actions_in_order()
    {
        var journal = Create();
        string session = journal.StartSession("Normal", "7-Zip");
        long a1 = journal.LogPending(session, JournalActionKind.QuarantinePath, @"C:\Program Files\7-Zip");
        long a2 = journal.LogPending(session, JournalActionKind.DeleteRegistryKey, @"HKLM\SOFTWARE\7-Zip");
        journal.Complete(a1, JournalActionStatus.Done, backupPath: @"C:\ProgramData\MyUninstaller\Karantina\x\1\7-Zip");
        journal.Complete(a2, JournalActionStatus.Failed, "akses ditolak");
        journal.FinishSession(session, JournalSessionStatus.Completed);

        session.Should().StartWith("20261114-143000-");
        var actions = journal.GetActions(session);
        actions.Select(a => a.Status).Should().Equal(JournalActionStatus.Done, JournalActionStatus.Failed);
        actions[0].BackupPath.Should().EndWith(@"1\7-Zip");
        actions[1].Detail.Should().Be("akses ditolak");

        var sessions = journal.GetSessions();
        sessions.Should().ContainSingle().Which.Status.Should().Be(JournalSessionStatus.Completed);
        sessions[0].StartedAt.Should().Be(_clock.Now);
    }

    [Fact]
    public void Unfinished_sessions_survive_reopening()
    {
        string session = Create().StartSession("Super", "VLC");

        var reopened = Create(); // seperti aplikasi dibuka lagi setelah crash
        reopened.GetUnfinishedSessions().Should().ContainSingle().Which.Id.Should().Be(session);

        reopened.FinishSession(session, JournalSessionStatus.Interrupted);
        reopened.GetUnfinishedSessions().Should().BeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}

/// <summary>Karantina sungguhan di folder sementara (tidak menyentuh berkas pengguna).</summary>
[Trait("Category", "Integration")]
public sealed class QuarantineTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "MyUninstallerTests", Guid.NewGuid().ToString("N"));

    // Folder sumber di drive tempat tes dijalankan (proyek ada di E:), untuk menguji jalur antar-drive.
    private readonly string _otherDrive = Path.Combine(AppContext.BaseDirectory, "tmp-karantina", Guid.NewGuid().ToString("N"));

    private QuarantineService Create(params string[] forbidden) =>
        new(Path.Combine(_work, "Karantina"), new ProtectedPaths(forbidden, []), NullLogger<QuarantineService>.Instance);

    private static void MakeProgram(string folder)
    {
        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        File.WriteAllText(Path.Combine(folder, "app.exe"), "exe");
        File.WriteAllText(Path.Combine(folder, "bin", "lib.dll"), "dll");
        File.WriteAllText(Path.Combine(folder, "readme.txt"), "baca");
        File.SetAttributes(Path.Combine(folder, "readme.txt"), FileAttributes.ReadOnly);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Folder_round_trip(bool crossDrive)
    {
        string root = crossDrive ? _otherDrive : _work;
        string program = Path.Combine(root, "Aplikasi Uji");
        MakeProgram(program);
        var quarantine = Create();

        var result = quarantine.Quarantine("20261114-143000-abc", program);

        result.Status.Should().Be(QuarantineStatus.Moved);
        Directory.Exists(program).Should().BeFalse();
        File.ReadAllText(Path.Combine(result.QuarantinePath!, "bin", "lib.dll")).Should().Be("dll");
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(result.QuarantinePath)!, "asal.txt")).Should().Be(program);
        result.Bytes.Should().Be(10);

        quarantine.Restore(result.QuarantinePath!, program).Should().BeTrue();
        File.ReadAllText(Path.Combine(program, "app.exe")).Should().Be("exe");
        File.ReadAllText(Path.Combine(program, "readme.txt")).Should().Be("baca");
    }

    [Fact]
    public void Single_file_round_trip()
    {
        Directory.CreateDirectory(_work);
        string file = Path.Combine(_work, "pintasan.lnk");
        File.WriteAllText(file, "lnk");
        var quarantine = Create();

        var result = quarantine.Quarantine("20261114-143000-abc", file);

        File.Exists(file).Should().BeFalse();
        quarantine.Restore(result.QuarantinePath!, file).Should().BeTrue();
        File.ReadAllText(file).Should().Be("lnk");
    }

    [Fact]
    public void Protected_path_is_never_moved()
    {
        string program = Path.Combine(_work, "Dilindungi");
        MakeProgram(program);

        var result = Create(forbidden: program).Quarantine("s", program);

        result.Status.Should().Be(QuarantineStatus.Skipped);
        Directory.Exists(program).Should().BeTrue();
    }

    [Fact]
    public void Junction_is_skipped_and_target_untouched()
    {
        string target = Path.Combine(_work, "TujuanAsli");
        MakeProgram(target);
        string junction = Path.Combine(_work, "Tautan");
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false })!)
        {
            mklink.WaitForExit();
        }

        Directory.Exists(junction).Should().BeTrue("mklink /J tidak butuh admin");
        Create().Quarantine("s", junction).Status.Should().Be(QuarantineStatus.Skipped);
        File.Exists(Path.Combine(target, "app.exe")).Should().BeTrue();
    }

    [Fact]
    public void Restore_does_not_overwrite_reinstalled_program()
    {
        string program = Path.Combine(_work, "App");
        MakeProgram(program);
        var quarantine = Create();
        var result = quarantine.Quarantine("s", program);

        Directory.CreateDirectory(program); // dipasang ulang
        quarantine.Restore(result.QuarantinePath!, program).Should().BeFalse();
        Directory.Exists(result.QuarantinePath).Should().BeTrue();
    }

    [Fact]
    public void Locked_file_in_cross_drive_folder_keeps_quarantine_copy()
    {
        string program = Path.Combine(_otherDrive, "Terkunci");
        MakeProgram(program);
        string locked = Path.Combine(program, "app.exe");

        QuarantineResult result;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = Create().Quarantine("s", program);
        }

        // Salinan lengkap ada di karantina, apa pun nasib berkas terkunci di sumber.
        result.Status.Should().BeOneOf(QuarantineStatus.Moved, QuarantineStatus.PendingReboot);
        File.ReadAllText(Path.Combine(result.QuarantinePath!, "app.exe")).Should().Be("exe");
        File.ReadAllText(Path.Combine(result.QuarantinePath!, "bin", "lib.dll")).Should().Be("dll");
        result.Detail.Should().NotBeNull("berkas terkunci harus dilaporkan");
    }

    [Fact]
    public void Purge_removes_only_expired_session_folders()
    {
        var quarantine = Create();
        string root = Path.Combine(_work, "Karantina");
        string old = Path.Combine(root, DateTime.Today.AddDays(-45).ToString("yyyyMMdd") + "-100000-aaaaaa");
        string recent = Path.Combine(root, DateTime.Today.AddDays(-3).ToString("yyyyMMdd") + "-100000-bbbbbb");
        string foreign = Path.Combine(root, "bukan-sesi");
        foreach (string d in new[] { old, recent, foreign })
        {
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "x.txt"), "x");
        }

        File.SetAttributes(Path.Combine(old, "x.txt"), FileAttributes.ReadOnly);

        quarantine.PurgeExpired(30).Should().Be(1);
        Directory.Exists(old).Should().BeFalse();
        Directory.Exists(recent).Should().BeTrue();
        Directory.Exists(foreign).Should().BeTrue();
    }

    public void Dispose()
    {
        foreach (string dir in new[] { _work, _otherDrive })
        {
            if (Directory.Exists(dir))
            {
                // Lepas junction dulu (hanya tautannya), supaya penghapusan rekursif tidak masuk ke tujuannya.
                foreach (var sub in new DirectoryInfo(dir).EnumerateDirectories("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
                {
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        sub.Delete();
                    }
                }

                foreach (string f in Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                }

                Directory.Delete(dir, recursive: true);
            }
        }
    }
}

/// <summary>
/// Cadangan registri sungguhan pada kunci uji milik tes sendiri:
/// HKCU\Software\MyUninstallerTests\&lt;guid&gt; (dibuat dan dihapus oleh tes).
/// </summary>
[Trait("Category", "Integration")]
public sealed class RegistryBackupTests : IDisposable
{
    private readonly string _testKey = @"Software\MyUninstallerTests\" + Guid.NewGuid().ToString("N");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MyUninstallerTests", Guid.NewGuid().ToString("N"));

    private string FullPath => @"HKEY_CURRENT_USER\" + _testKey;

    [Fact]
    public void Export_delete_import_round_trip()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(_testKey))
        {
            key.SetValue("DisplayName", "Aplikasi Uji");
            key.SetValue("EstimatedSize", 1234, RegistryValueKind.DWord);
            using var sub = key.CreateSubKey("Pengaturan");
            sub.SetValue("Tema", "gelap");
        }

        var service = new RegistryBackupService(_dir, NullLogger<RegistryBackupService>.Instance);

        string? backup = service.Export("s", FullPath);
        backup.Should().NotBeNull();
        File.ReadAllText(backup!).Should().Contain("Aplikasi Uji");

        service.DeleteKey(FullPath).Should().BeTrue();
        service.KeyExists(FullPath).Should().BeFalse();

        service.Import(backup!).Should().BeTrue();
        using var restored = Registry.CurrentUser.OpenSubKey(_testKey)!;
        restored.GetValue("DisplayName").Should().Be("Aplikasi Uji");
        restored.GetValue("EstimatedSize").Should().Be(1234);
        restored.OpenSubKey("Pengaturan")!.GetValue("Tema").Should().Be("gelap");
    }

    [Fact]
    public void Protected_key_is_never_deleted() =>
        new RegistryBackupService(_dir, NullLogger<RegistryBackupService>.Instance)
            .DeleteKey(@"HKEY_CURRENT_USER\Software\Microsoft").Should().BeFalse();

    [Fact]
    public void Export_of_missing_key_returns_null() =>
        new RegistryBackupService(_dir, NullLogger<RegistryBackupService>.Instance)
            .Export("s", FullPath + @"\TidakAda").Should().BeNull();

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_testKey, throwOnMissingSubKey: false);
        using (var parent = Registry.CurrentUser.OpenSubKey(@"Software\MyUninstallerTests", writable: true))
        {
            if (parent is { SubKeyCount: 0, ValueCount: 0 })
            {
                Registry.CurrentUser.DeleteSubKey(@"Software\MyUninstallerTests", throwOnMissingSubKey: false);
            }
        }

        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}

/// <summary>SafeRemover + SessionRestorer dengan jurnal dan karantina sungguhan, registri palsu.</summary>
[Trait("Category", "Integration")]
public sealed class SafeRemoverTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "MyUninstallerTests", Guid.NewGuid().ToString("N"));

    private sealed class FakeRegistry(bool exportWorks = true) : IRegistryBackupService
    {
        public HashSet<string> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Deleted { get; } = [];

        public List<string> Imported { get; } = [];

        public bool KeyExists(string keyPath) => Keys.Contains(keyPath);

        public string? Export(string sessionId, string keyPath) => exportWorks ? $@"C:\fake\{Keys.Count}.reg" : null;

        public bool DeleteKey(string keyPath)
        {
            Deleted.Add(keyPath);
            return Keys.Remove(keyPath);
        }

        public bool Import(string regFile)
        {
            Imported.Add(regFile);
            return true;
        }

        public HashSet<string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool ValueExists(string keyPath, string valueName) => Values.Contains($@"{keyPath}\{valueName}");

        public bool DeleteValue(string keyPath, string valueName)
        {
            Deleted.Add($@"{keyPath}\{valueName}");
            return Values.Remove($@"{keyPath}\{valueName}");
        }
    }

    private (SafeRemover Remover, SqliteSessionJournal Journal, QuarantineService Quarantine) Create(FakeRegistry registry, params string[] forbidden)
    {
        var protectedPaths = new ProtectedPaths(forbidden, []);
        var journal = new SqliteSessionJournal(Path.Combine(_work, "riwayat.db"), new FixedClock(DateTimeOffset.Now));
        var quarantine = new QuarantineService(Path.Combine(_work, "Karantina"), protectedPaths, NullLogger<QuarantineService>.Instance);
        return (new SafeRemover(protectedPaths, quarantine, registry, journal), journal, quarantine);
    }

    private const string Key = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Uji";

    [Fact]
    public void Removes_folder_and_key_then_restores_whole_session()
    {
        string program = Path.Combine(_work, "Program Uji");
        Directory.CreateDirectory(program);
        File.WriteAllText(Path.Combine(program, "app.exe"), "exe");
        var registry = new FakeRegistry();
        registry.Keys.Add(Key);
        var (remover, journal, quarantine) = Create(registry);
        string session = journal.StartSession("Force", "Program Uji");

        var results = remover.Remove(session,
        [
            new RemovalTarget(RemovalTargetKind.Path, program),
            new RemovalTarget(RemovalTargetKind.RegistryKey, Key),
        ]);

        results.Select(r => r.Status).Should().Equal(RemovalStatus.Removed, RemovalStatus.Removed);
        Directory.Exists(program).Should().BeFalse();
        registry.Keys.Should().BeEmpty();
        journal.GetActions(session).Should().OnlyContain(a => a.Status == JournalActionStatus.Done && a.BackupPath != null);

        var summary = new SessionRestorer(journal, quarantine, registry).Restore(session);

        summary.Should().Be(new RestoreSummary(1, 1, 0));
        File.ReadAllText(Path.Combine(program, "app.exe")).Should().Be("exe");
        registry.Imported.Should().ContainSingle();
        journal.GetSessions().Single().Status.Should().Be(JournalSessionStatus.Restored);
        journal.GetActions(session).Should().OnlyContain(a => a.Status == JournalActionStatus.Restored);
    }

    [Fact]
    public void Key_is_not_deleted_when_backup_fails()
    {
        var registry = new FakeRegistry(exportWorks: false);
        registry.Keys.Add(Key);
        var (remover, journal, _) = Create(registry);
        string session = journal.StartSession("Force", "x");

        var result = remover.Remove(session, [new RemovalTarget(RemovalTargetKind.RegistryKey, Key)]).Single();

        result.Status.Should().Be(RemovalStatus.Failed);
        registry.Deleted.Should().BeEmpty();
        journal.GetActions(session).Single().Status.Should().Be(JournalActionStatus.Failed);
    }

    [Fact]
    public void Protected_targets_are_refused_without_journal_entries()
    {
        string program = Path.Combine(_work, "Dilindungi");
        Directory.CreateDirectory(program);
        var registry = new FakeRegistry();
        registry.Keys.Add(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows");
        var (remover, journal, _) = Create(registry, forbidden: program);
        string session = journal.StartSession("Force", "x");

        var results = remover.Remove(session,
        [
            new RemovalTarget(RemovalTargetKind.Path, program),
            new RemovalTarget(RemovalTargetKind.RegistryKey, @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows"),
        ]);

        results.Should().OnlyContain(r => r.Status == RemovalStatus.Protected);
        Directory.Exists(program).Should().BeTrue();
        registry.Deleted.Should().BeEmpty();
        journal.GetActions(session).Should().BeEmpty();
    }

    [Fact]
    public void Missing_targets_are_reported_not_failed()
    {
        var (remover, journal, _) = Create(new FakeRegistry());
        string session = journal.StartSession("Force", "x");

        remover.Remove(session,
        [
            new RemovalTarget(RemovalTargetKind.Path, Path.Combine(_work, "tidak-ada")),
            new RemovalTarget(RemovalTargetKind.RegistryKey, Key),
        ]).Should().OnlyContain(r => r.Status == RemovalStatus.Missing);
    }

    private const string RunKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run";

    [Fact]
    public void Run_value_is_backed_up_then_deleted()
    {
        var registry = new FakeRegistry();
        registry.Values.Add($@"{RunKey}\AppTray");
        var (remover, journal, _) = Create(registry);
        string session = journal.StartSession("Normal", "x");

        var result = remover.Remove(session, [new RemovalTarget(RemovalTargetKind.RegistryValue, RunKey, "AppTray")]).Single();

        result.Status.Should().Be(RemovalStatus.Removed);
        registry.Values.Should().BeEmpty();
        journal.GetActions(session).Single().Should().Match<JournalAction>(a =>
            a.Kind == JournalActionKind.DeleteRegistryValue && a.Status == JournalActionStatus.Done && a.BackupPath != null);
    }

    [Theory]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA")]
    [InlineData(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run", "")]
    public void Protected_values_are_refused(string key, string value)
    {
        var registry = new FakeRegistry();
        registry.Values.Add($@"{key}\{value}");
        var (remover, journal, _) = Create(registry);
        string session = journal.StartSession("Normal", "x");

        remover.Remove(session, [new RemovalTarget(RemovalTargetKind.RegistryValue, key, value)])
            .Single().Status.Should().Be(RemovalStatus.Protected);
        registry.Deleted.Should().BeEmpty();
    }

    [Fact]
    public void Windows_service_is_refused()
    {
        var services = new FakeServices(new ServiceInfo("Spooler", @"C:\Windows\System32\spoolsv.exe", 0x10, @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Spooler"));
        var protectedPaths = new ProtectedPaths([], []);
        var journal = new SqliteSessionJournal(Path.Combine(_work, "riwayat.db"), new FixedClock(DateTimeOffset.Now));
        var remover = new SafeRemover(protectedPaths, null!, new FakeRegistry(), journal, services);

        remover.Remove(journal.StartSession("Super", "x"), [new RemovalTarget(RemovalTargetKind.Service, "Spooler")])
            .Single().Status.Should().Be(RemovalStatus.Protected);
        services.Deleted.Should().BeEmpty();
    }

    [Fact]
    public void Third_party_service_is_backed_up_then_deleted()
    {
        var services = new FakeServices(new ServiceInfo("AppUpdater", @"D:\Apps\App\updater.exe", 0x10, @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\AppUpdater"));
        var journal = new SqliteSessionJournal(Path.Combine(_work, "riwayat.db"), new FixedClock(DateTimeOffset.Now));
        var remover = new SafeRemover(new ProtectedPaths([], []), null!, new FakeRegistry(), journal, services);
        string session = journal.StartSession("Super", "x");

        remover.Remove(session, [new RemovalTarget(RemovalTargetKind.Service, "AppUpdater")]).Single().Status.Should().Be(RemovalStatus.Removed);
        services.Deleted.Should().Equal("AppUpdater");
        journal.GetActions(session).Single().BackupPath.Should().NotBeNull();
    }

    private sealed class FakeServices(params ServiceInfo[] services) : IServiceRemover
    {
        public List<string> Deleted { get; } = [];

        public ServiceInfo? Get(string serviceName) => services.FirstOrDefault(s => s.Name == serviceName);

        public bool StopAndDelete(string serviceName)
        {
            Deleted.Add(serviceName);
            return true;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_work))
        {
            Directory.Delete(_work, recursive: true);
        }
    }
}

public class ForceUninstallPlannerTests
{
    private static readonly InstallLocationRules Rules = new([@"C:\Program Files"]);
    private static readonly ProtectedPaths Protected = new([@"C:\Windows"], [@"C:\Program Files"]);

    private static InstalledProgram P(string? location, string? key, ProgramSource source = ProgramSource.Registry64) => new()
    {
        Id = "x",
        DisplayName = "x",
        Source = source,
        InstallLocation = location,
        RegistryKeyPath = key,
    };

    [Fact]
    public void Plans_folder_and_uninstall_key()
    {
        var targets = ForceUninstallPlanner.Plan(
            P(@"C:\Program Files\App", @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\App"),
            Rules, Protected, _ => true);

        targets.Should().Equal(
            new RemovalTarget(RemovalTargetKind.Path, @"C:\Program Files\App"),
            new RemovalTarget(RemovalTargetKind.RegistryKey, @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\App"));
    }

    [Theory]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Windows\App")]
    [InlineData(null)]
    public void Never_plans_shared_or_protected_folders(string? location) =>
        ForceUninstallPlanner.Plan(P(location, null), Rules, Protected, _ => true).Should().BeEmpty();

    [Fact]
    public void Missing_folder_is_not_planned() =>
        ForceUninstallPlanner.Plan(P(@"C:\Program Files\App", null), Rules, Protected, _ => false).Should().BeEmpty();

    [Fact]
    public void Broken_entry_plans_only_registry_key() =>
        ForceUninstallPlanner.PlanBrokenEntry(P(@"C:\Program Files\App", @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall\App"))
            .Should().ContainSingle().Which.Kind.Should().Be(RemovalTargetKind.RegistryKey);
}

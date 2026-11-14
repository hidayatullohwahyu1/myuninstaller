using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using MyUninstaller.App.Services;
using MyUninstaller.App.ViewModels;
using MyUninstaller.App.Views;
using MyUninstaller.Core.Abstractions;
using MyUninstaller.Core.Safety;
using MyUninstaller.Core.Settings;
using MyUninstaller.Infrastructure.FileSystem;
using MyUninstaller.Infrastructure.Safety;
using MyUninstaller.Tests.Safety;

namespace MyUninstaller.Tests.Ui;

public class HistoryReportTests
{
    private static JournalAction A(long id, JournalActionKind kind, JournalActionStatus status, string? backup = "ada") =>
        new(id, "s", kind, $"target-{id}", backup, status, null, DateTimeOffset.Now);

    private static readonly JournalAction[] Actions =
    [
        A(1, JournalActionKind.RestorePoint, JournalActionStatus.Skipped, null),
        A(2, JournalActionKind.RunUninstaller, JournalActionStatus.Done, null),
        A(3, JournalActionKind.QuarantinePath, JournalActionStatus.Done),
        A(4, JournalActionKind.QuarantinePath, JournalActionStatus.Restored),
        A(5, JournalActionKind.DeleteRegistryKey, JournalActionStatus.Done),
        A(6, JournalActionKind.DeleteRegistryValue, JournalActionStatus.Done, "hilang"),
        A(7, JournalActionKind.DeleteService, JournalActionStatus.Failed),
        A(8, JournalActionKind.DeleteScheduledTask, JournalActionStatus.Done),
    ];

    private static bool Exists(string path) => path == "ada";

    [Fact]
    public void Summarize_counts_by_kind_and_restorable_backups()
    {
        HistoryReport.Summarize(Actions, Exists).Should().Be(new SessionSummary(
            Paths: 2, RegistryItems: 2, ServicesAndTasks: 1, Uninstallers: 1, Failed: 1, Restored: 1,
            Restorable: 3)); // 3 (karantina), 5 (kunci), 8 (tugas). 6: cadangan hilang. 7: gagal. 4: sudah dipulihkan.
    }

    [Theory]
    [InlineData(JournalSessionStatus.Completed, 1, true)]
    [InlineData(JournalSessionStatus.Interrupted, 1, true)]
    [InlineData(JournalSessionStatus.Running, 1, false)]
    [InlineData(JournalSessionStatus.Completed, 0, false)]
    public void CanUndo(JournalSessionStatus status, int restorable, bool expected)
    {
        var session = new JournalSession("s", DateTimeOffset.Now, null, "Normal", "Uji", status, null);
        HistoryReport.CanUndo(session, new SessionSummary(0, 0, 0, 0, 0, 0, restorable)).Should().Be(expected);
    }

    [Fact]
    public void Report_contains_session_actions_and_backups()
    {
        var session = new JournalSession("20261114-143000-abcdef", new DateTimeOffset(2026, 11, 14, 14, 30, 0, TimeSpan.FromHours(7)),
            null, "Normal", "7-Zip", JournalSessionStatus.Completed, null);

        string text = HistoryReport.BuildText(session, Actions, "1.0.0", @"C:\ProgramData\MyUninstaller\Log",
            s => s.ToString(), k => k.ToString(), a => a.ToString());

        text.Should().Contain("Sesi: 20261114-143000-abcdef")
            .And.Contain("Judul: 7-Zip")
            .And.Contain("[Done] QuarantinePath: target-3")
            .And.Contain("cadangan: ada")
            .And.Contain(@"Catatan aktivitas lengkap: C:\ProgramData\MyUninstaller\Log");
    }
}

/// <summary>
/// Kriteria penerimaan PRD "Batalkan mengembalikan semua berkas dan registri dari satu sesi; program yang
/// dipulihkan tetap tercatat di registri": karantina, reg.exe, jurnal SQLite, dan Riwayat sungguhan pada
/// folder sementara dan kunci HKCU\Software\MyUninstallerTests\&lt;guid&gt; (dibuat dan dihapus tes sendiri).
/// </summary>
[Trait("Category", "Integration")]
public sealed class HistoryIntegrationTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "MyUninstallerTests", Guid.NewGuid().ToString("N"));
    private readonly string _testKey = @"Software\MyUninstallerTests\" + Guid.NewGuid().ToString("N");

    private sealed class FakeShell : IShellService
    {
        public string? Copied { get; private set; }

        public string? Opened { get; private set; }

        public bool OpenFolder(string path)
        {
            Opened = path;
            return true;
        }

        public bool OpenUrl(string url) => true;

        public void CopyText(string text) => Copied = text;

        public bool OpenRegistryEditor(string keyPath) => true;
    }

    private sealed class Settings : ISettingsStore
    {
        public AppSettings Load() => new();

        public void Save(AppSettings settings)
        {
        }
    }

    private sealed record Env(
        SqliteSessionJournal Journal,
        QuarantineService Quarantine,
        RegistryBackupService Registry,
        SafeRemover Remover,
        SessionRestorer Restorer);

    private Env Create()
    {
        var protectedPaths = new ProtectedPaths([], []);
        var journal = new SqliteSessionJournal(Path.Combine(_work, "riwayat.db"), new FixedClock(DateTimeOffset.Now));
        var quarantine = new QuarantineService(Path.Combine(_work, "Karantina"), protectedPaths, NullLogger<QuarantineService>.Instance);
        var registry = new RegistryBackupService(Path.Combine(_work, "Karantina"), NullLogger<RegistryBackupService>.Instance);
        return new Env(journal, quarantine, registry,
            new SafeRemover(protectedPaths, quarantine, registry, journal),
            new SessionRestorer(journal, quarantine, registry));
    }

    /// <summary>Program uji: folder berisi berkas + kunci "Uninstall" di bawah kunci uji.</summary>
    private (string Folder, string KeyPath) CreateProgram(string name)
    {
        string folder = Path.Combine(_work, "Program Files", name);
        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        File.WriteAllText(Path.Combine(folder, "app.exe"), "exe");
        File.WriteAllText(Path.Combine(folder, "bin", "lib.dll"), "dll");

        string subKey = $@"{_testKey}\Uninstall\{name}";
        using (var key = Registry.CurrentUser.CreateSubKey(subKey))
        {
            key.SetValue("DisplayName", name);
            key.SetValue("InstallLocation", folder);
        }

        return (folder, @"HKEY_CURRENT_USER\" + subKey);
    }

    private HistoryViewModel History(Env env, FakeShell shell, bool confirm = true) => new(
        env.Journal, env.Restorer, env.Quarantine, new FolderSizeCalculator(), shell, new Settings(),
        new HistoryDialogs(_ => confirm, (_, _) => confirm), Path.Combine(_work, "Log"), NullLogger.Instance);

    [StaFact]
    public async Task Undo_restores_all_files_and_registry_of_one_session()
    {
        var env = Create();
        var (folder, key) = CreateProgram("Aplikasi Uji");
        string session = env.Journal.StartSession("Force", "Aplikasi Uji");
        env.Remover.Remove(session,
        [
            new RemovalTarget(RemovalTargetKind.Path, folder),
            new RemovalTarget(RemovalTargetKind.RegistryKey, key),
        ]);
        env.Journal.FinishSession(session, JournalSessionStatus.Completed);
        Directory.Exists(folder).Should().BeFalse();
        env.Registry.KeyExists(key).Should().BeFalse();

        var history = History(env, new FakeShell());
        history.Sessions.Should().ContainSingle();
        history.Selected!.Title.Should().Be("Aplikasi Uji");
        history.SummaryText.Should().Be("1 berkas/folder · 1 entri registri · 0 layanan/tugas");
        history.RestorableText.Should().Be("2 item bisa dipulihkan");
        history.UndoCommand.CanExecute(null).Should().BeTrue();

        await history.UndoCommand.ExecuteAsync(null);

        // Berkas kembali utuh dan program kembali tercatat di registri.
        File.ReadAllText(Path.Combine(folder, "bin", "lib.dll")).Should().Be("dll");
        using (var restored = Registry.CurrentUser.OpenSubKey(key["HKEY_CURRENT_USER\\".Length..]))
        {
            restored.Should().NotBeNull();
            restored!.GetValue("DisplayName").Should().Be("Aplikasi Uji");
        }

        history.AnyRestored.Should().BeTrue();
        history.NoticeText.Should().Be("Sesi dibatalkan: 1 berkas/folder dan 1 entri dipulihkan.");
        history.Selected!.StatusText.Should().Be("Dibatalkan");
        history.UndoCommand.CanExecute(null).Should().BeFalse("tidak ada lagi yang bisa dipulihkan");
    }

    [StaFact]
    public void Purge_empties_session_quarantine_and_disables_undo()
    {
        var env = Create();
        var (folder, _) = CreateProgram("Aplikasi Dua");
        string session = env.Journal.StartSession("Normal", "Aplikasi Dua");
        env.Remover.Remove(session, [new RemovalTarget(RemovalTargetKind.Path, folder)]);
        env.Journal.FinishSession(session, JournalSessionStatus.Completed);

        var shell = new FakeShell();
        var history = History(env, shell);
        history.OpenQuarantineCommand.Execute(null);
        shell.Opened.Should().Be(env.Quarantine.SessionFolder(session));

        history.PurgeCommand.Execute(null);

        Directory.Exists(env.Quarantine.SessionFolder(session)).Should().BeFalse();
        history.Selected!.Session.Note.Should().Be("Karantina dikosongkan oleh pengguna");
        history.RestorableText.Should().Be("Tidak ada yang bisa dipulihkan dari sesi ini.");
        history.UndoCommand.CanExecute(null).Should().BeFalse();
    }

    [StaFact]
    public void Declined_confirmation_changes_nothing()
    {
        var env = Create();
        var (folder, _) = CreateProgram("Aplikasi Tiga");
        string session = env.Journal.StartSession("Normal", "Aplikasi Tiga");
        env.Remover.Remove(session, [new RemovalTarget(RemovalTargetKind.Path, folder)]);
        env.Journal.FinishSession(session, JournalSessionStatus.Completed);

        var history = History(env, new FakeShell(), confirm: false);
        history.PurgeCommand.Execute(null);

        Directory.Exists(env.Quarantine.SessionFolder(session)).Should().BeTrue();
        history.RestorableText.Should().Be("1 item bisa dipulihkan");
    }

    [StaFact]
    public void Copy_report_puts_text_on_clipboard_service()
    {
        var env = Create();
        string session = env.Journal.StartSession("Builtin", "Aplikasi Empat");
        long action = env.Journal.LogPending(session, JournalActionKind.RunUninstaller, @"""C:\App\uninst.exe""");
        env.Journal.Complete(action, JournalActionStatus.Done, "Success, kode 0");
        env.Journal.FinishSession(session, JournalSessionStatus.Completed);

        var shell = new FakeShell();
        var history = History(env, shell);
        history.CopyReportCommand.Execute(null);

        shell.Copied.Should().Contain("Judul: Aplikasi Empat")
            .And.Contain("Mode: Builtin")
            .And.Contain(@"[Selesai] Penghapus instalan: ""C:\App\uninst.exe"" (Success, kode 0)");
        history.NoticeText.Should().Be("Laporan disalin ke papan klip.");
    }

    [Fact]
    public void Invalid_session_id_is_rejected()
    {
        var quarantine = new QuarantineService(Path.Combine(_work, "Karantina"), new ProtectedPaths([], []), NullLogger<QuarantineService>.Instance);

        FluentActions.Invoking(() => quarantine.PurgeSession(@"..\..\Windows")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => quarantine.SessionFolder(@"C:\Windows")).Should().Throw<ArgumentException>();
    }

    [StaFact]
    public void History_window_loads()
    {
        WindowXamlTests.EnsureApplicationResources();
        var window = new HistoryWindow(History(Create(), new FakeShell()));

        window.Title.Should().Be("Riwayat");
        window.Close();
    }

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

        if (Directory.Exists(_work))
        {
            foreach (string file in Directory.EnumerateFiles(_work, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_work, recursive: true);
        }
    }
}

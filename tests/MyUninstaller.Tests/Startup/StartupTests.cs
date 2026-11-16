using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using MyUninstaller.App.Services;
using MyUninstaller.App.ViewModels;
using MyUninstaller.App.Views;
using MyUninstaller.Core.Models;
using MyUninstaller.Core.Startup;
using MyUninstaller.Core.Usage;
using MyUninstaller.Infrastructure.Leftovers;
using MyUninstaller.Infrastructure.Startup;
using MyUninstaller.Tests.Safety;
using MyUninstaller.Tests.Ui;

namespace MyUninstaller.Tests.Startup;

public class StartupApprovedStateTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData(new byte[0], true)]
    [InlineData(new byte[] { 0x02, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x06, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x03, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x07, 0, 0, 0 }, false)]
    public void IsEnabled(byte[]? data, bool expected) => StartupApprovedState.IsEnabled(data).Should().Be(expected);

    [Fact]
    public void Disabled_value_records_time_and_round_trips()
    {
        var now = new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

        byte[] disabled = StartupApprovedState.Create(enabled: false, now);
        byte[] enabled = StartupApprovedState.Create(enabled: true, now);

        disabled.Should().HaveCount(12);
        disabled[0].Should().Be(0x03);
        StartupApprovedState.IsEnabled(disabled).Should().BeFalse();
        StartupApprovedState.DisabledAt(disabled).Should().Be(now);
        enabled.Should().Equal(0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        StartupApprovedState.DisabledAt(enabled).Should().BeNull();
    }
}

/// <summary>Pembaca program awal pada kunci HKCU\Software\MyUninstallerTests\&lt;guid&gt; dan folder sementara.</summary>
[Trait("Category", "Integration")]
public sealed class StartupRepositoryTests : IDisposable
{
    private readonly string _regRoot = @"Software\MyUninstallerTests\" + Guid.NewGuid().ToString("N");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MyUninstallerTests", Guid.NewGuid().ToString("N"));

    private string StartupFolder => Path.Combine(_dir, "Startup");

    private string TasksFolder => Path.Combine(_dir, "Tasks");

    private StartupRepository Create() => new(
        new StartupLocations(
            [new RunLocation(StartupSource.RunUser, new RegistryRoot(RegistryHive.CurrentUser, _regRoot + @"\Run"),
                new RegistryRoot(RegistryHive.CurrentUser, _regRoot + @"\Approved\Run"))],
            [new FolderLocation(StartupSource.StartupFolderUser, StartupFolder,
                new RegistryRoot(RegistryHive.CurrentUser, _regRoot + @"\Approved\StartupFolder"))],
            TasksFolder),
        new FixedClock(new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero)),
        NullLogger<StartupRepository>.Instance);

    private const string LogonTask = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers>
          <Settings><Enabled>false</Enabled></Settings>
          <Actions><Exec><Command>C:\Program Files\Vendor\agent.exe</Command><Arguments>--tray</Arguments></Exec></Actions>
        </Task>
        """;

    private const string DailyTask = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Triggers><CalendarTrigger /></Triggers>
          <Actions><Exec><Command>C:\Vendor\update.exe</Command></Exec></Actions>
        </Task>
        """;

    private void Arrange()
    {
        using (var run = Registry.CurrentUser.CreateSubKey(_regRoot + @"\Run"))
        {
            run.SetValue("AppTray", @"""C:\Program Files\App\tray.exe"" /min");
            run.SetValue("Lama", @"C:\Lama\lama.exe");
        }

        using (var approved = Registry.CurrentUser.CreateSubKey(_regRoot + @"\Approved\Run"))
        {
            approved.SetValue("Lama", StartupApprovedState.Create(false, DateTimeOffset.Now), RegistryValueKind.Binary);
        }

        Directory.CreateDirectory(StartupFolder);
        File.WriteAllText(Path.Combine(StartupFolder, "catatan.bat"), "@echo off");
        File.WriteAllText(Path.Combine(StartupFolder, "desktop.ini"), "[.ShellClassInfo]");

        Directory.CreateDirectory(Path.Combine(TasksFolder, "Vendor"));
        Directory.CreateDirectory(Path.Combine(TasksFolder, "Microsoft", "Windows"));
        File.WriteAllText(Path.Combine(TasksFolder, "Vendor", "Agent"), LogonTask, System.Text.Encoding.Unicode);
        File.WriteAllText(Path.Combine(TasksFolder, "Vendor", "Harian"), DailyTask, System.Text.Encoding.Unicode);
        File.WriteAllText(Path.Combine(TasksFolder, "Microsoft", "Windows", "Sistem"), LogonTask, System.Text.Encoding.Unicode);
    }

    [Fact]
    public void Reads_run_folder_and_logon_tasks_only()
    {
        Arrange();

        var entries = Create().GetAll();

        entries.Select(e => (e.Name, e.Source, e.IsEnabled)).Should().BeEquivalentTo(new[]
        {
            ("AppTray", StartupSource.RunUser, true),
            ("Lama", StartupSource.RunUser, false),
            ("catatan", StartupSource.StartupFolderUser, true),
            ("Agent", StartupSource.ScheduledTask, false),
        });

        entries.Single(e => e.Name == "AppTray").ExecutablePath.Should().Be(@"C:\Program Files\App\tray.exe");
        var task = entries.Single(e => e.Name == "Agent");
        task.Location.Should().Be(@"\Vendor\Agent");
        task.Command.Should().Be(@"""C:\Program Files\Vendor\agent.exe"" --tray");
    }

    [Fact]
    public void Disable_and_enable_write_startup_approved_without_touching_run_value()
    {
        Arrange();
        var repository = Create();
        var entry = repository.GetAll().Single(e => e.Name == "AppTray");

        repository.SetEnabled(entry, enabled: false).Should().BeTrue();
        repository.GetAll().Single(e => e.Name == "AppTray").IsEnabled.Should().BeFalse();

        using (var run = Registry.CurrentUser.OpenSubKey(_regRoot + @"\Run")!)
        {
            run.GetValue("AppTray").Should().Be(@"""C:\Program Files\App\tray.exe"" /min", "entri asli tidak pernah dihapus");
        }

        repository.SetEnabled(entry, enabled: true).Should().BeTrue();
        repository.GetAll().Single(e => e.Name == "AppTray").IsEnabled.Should().BeTrue();

        var folderEntry = repository.GetAll().Single(e => e.Name == "catatan");
        repository.SetEnabled(folderEntry, false).Should().BeTrue();
        File.Exists(Path.Combine(StartupFolder, "catatan.bat")).Should().BeTrue();
        repository.GetAll().Single(e => e.Name == "catatan").IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void Microsoft_task_is_never_toggled()
    {
        var entry = new StartupEntry
        {
            Name = "Sistem",
            Command = "x",
            Source = StartupSource.ScheduledTask,
            Location = @"\Microsoft\Windows\Sistem",
            IsEnabled = true,
        };

        Create().SetEnabled(entry, false).Should().BeFalse();
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

        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}

public class UsageIndexTests
{
    [Theory]
    [InlineData("{6Q809377-6NS0-444O-8957-N3773S02200R}\\IvqrbYNA\\IYP\\iyp.rkr", "{6D809377-6AF0-444B-8957-A3773F02200E}\\VideoLAN\\VLC\\vlc.exe")]
    [InlineData("Uryyb, Qhavn!", "Hello, Dunia!")]
    public void Rot13(string encoded, string decoded) => UsageIndex.Rot13(encoded).Should().Be(decoded);

    [Theory]
    [InlineData("CHROME.EXE-ABCD1234.pf", "CHROME.EXE")]
    [InlineData("MY-APP.EXE-0F00BA77.pf", "MY-APP.EXE")]
    [InlineData("tanpa-hash", "TANPA")]
    [InlineData("tanpatanda", null)]
    public void Prefetch_key_from_file(string file, string? expected) => UsageIndex.PrefetchKeyFromFile(file).Should().Be(expected);

    [Fact]
    public void Prefetch_key_is_truncated_like_windows() =>
        UsageIndex.PrefetchKey("SangatPanjangSekaliNamaProgramnya.exe").Should().Be("SANGATPANJANGSEKALINAMAPROGRA").And.HaveLength(29);

    [Fact]
    public void User_assist_time_is_read_from_offset_60()
    {
        var when = new DateTimeOffset(2026, 9, 2, 14, 30, 0, TimeSpan.Zero);
        var data = new byte[72];
        BitConverter.GetBytes(when.ToFileTime()).CopyTo(data, 60);

        UsageIndex.UserAssistLastRun(data).Should().Be(when);
        UsageIndex.UserAssistLastRun(new byte[16]).Should().BeNull();
        UsageIndex.UserAssistLastRun(new byte[72]).Should().BeNull();
    }

    [Fact]
    public void Last_used_takes_latest_evidence_and_ignores_uninstallers()
    {
        var older = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var newer = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var index = new UsageIndex(
            new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase) { [@"D:\App\app.exe"] = older },
            new Dictionary<string, DateTimeOffset> { ["APP.EXE"] = newer, ["UNINS000.EXE"] = DateTimeOffset.Now });

        index.LastUsed([@"D:\App\app.exe"]).Should().Be(newer);
        index.LastUsed([@"D:\App\unins000.exe"]).Should().BeNull("menjalankan uninstaller bukan memakai program");
        index.LastUsed([@"D:\App\lain.exe"]).Should().BeNull();
    }
}

[Collection(WpfCollection.Name)]
public class StartupUiTests
{
    private sealed class FakeRepository(bool succeed) : IStartupRepository
    {
        public List<(string Name, bool Enabled)> Calls { get; } = [];

        public IReadOnlyList<StartupEntry> GetAll() =>
        [
            new() { Name = "Tray", Command = @"D:\Apps\App\tray.exe", Source = StartupSource.RunUser, Location = "k", ApprovalName = "Tray", IsEnabled = true, ExecutablePath = @"D:\Apps\App\tray.exe" },
            new() { Name = "Lain", Command = @"D:\Lain\x.exe", Source = StartupSource.ScheduledTask, Location = @"\Lain", IsEnabled = false },
        ];

        public bool SetEnabled(StartupEntry entry, bool enabled)
        {
            Calls.Add((entry.Name, enabled));
            return succeed;
        }
    }

    private sealed class NoShell : IShellService
    {
        public bool OpenFolder(string path) => true;

        public bool OpenUrl(string url) => true;

        public void CopyText(string text)
        {
        }

        public bool OpenRegistryEditor(string keyPath) => true;
    }

    private static readonly InstalledProgram App = new()
    {
        Id = "App",
        DisplayName = "Aplikasi Uji",
        Source = ProgramSource.Registry64,
        InstallLocation = @"D:\Apps\App",
    };

    [StaFact]
    public void Lists_entries_matches_programs_and_toggles()
    {
        var repository = new FakeRepository(succeed: true);
        var vm = new StartupViewModel(repository, new NoShell(), () => [App], null, NullLogger.Instance);

        // Exe uji (D:\Apps\App\tray.exe) tidak ada di disk: rincian masalah diuji di StartupCleanupTests.
        vm.SummaryText.Should().StartWith("2 program awal · 1 aktif");
        vm.Items[0].ProgramText.Should().Be("Program: Aplikasi Uji");
        vm.Items[1].SourceText.Should().Be("Tugas terjadwal");

        vm.Items[0].IsEnabled = false;

        repository.Calls.Should().Equal(("Tray", false));
        vm.SummaryText.Should().StartWith("2 program awal · 0 aktif");
        vm.NoticeText.Should().Be("\"Tray\" dinonaktifkan; berlaku saat Windows dimulai berikutnya.");
    }

    [StaFact]
    public void Failed_toggle_is_reverted()
    {
        var vm = new StartupViewModel(new FakeRepository(succeed: false), new NoShell(), () => [], null, NullLogger.Instance);

        vm.Items[0].IsEnabled = false;

        vm.Items[0].IsEnabled.Should().BeTrue("tampilan dikembalikan bila gagal");
        vm.NoticeText.Should().Contain("tidak bisa diubah");
    }

    [StaFact]
    public void Rarely_used_label_appears_only_with_evidence()
    {
        var today = new DateOnly(2026, 11, 15);
        var item = new ProgramItemViewModel(App, today);
        item.IsRarelyUsed.Should().BeFalse("tanpa bukti pemakaian tidak dianggap jarang");

        item.ApplyLastUsed(today.AddDays(-120), today);

        item.IsRarelyUsed.Should().BeTrue();
        item.SummaryText.Should().Contain("terakhir dipakai 18 Juli 2026");

        item.ApplyLastUsed(today.AddDays(-3), today);
        item.IsRarelyUsed.Should().BeFalse();
    }

    [StaFact]
    public void Startup_window_loads()
    {
        WindowXamlTests.EnsureApplicationResources();
        var window = new StartupWindow(new StartupViewModel(new FakeRepository(true), new NoShell(), () => [], null, NullLogger.Instance));

        window.Title.Should().Be("Program awal");
        window.Close();
    }
}

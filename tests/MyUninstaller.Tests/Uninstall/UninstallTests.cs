using Microsoft.Extensions.Logging.Abstractions;
using MyUninstaller.Core.Hunter;
using MyUninstaller.Core.Models;
using MyUninstaller.Core.Safety;
using MyUninstaller.Core.Scanning;
using MyUninstaller.Core.Uninstall;
using MyUninstaller.Infrastructure.Processes;

namespace MyUninstaller.Tests.Uninstall;

public class UninstallCommandBuilderTests
{
    private static readonly Guid SevenZip = Guid.Parse("23170F69-40C1-2702-2409-000001000000");

    private static InstalledProgram P(
        string? uninstall = null, string? quiet = null, bool msi = false, Guid? productCode = null,
        ProgramSource source = ProgramSource.Registry64, string id = "Registry64:App") => new()
    {
        Id = id,
        DisplayName = "App",
        Source = source,
        UninstallString = uninstall,
        QuietUninstallString = quiet,
        IsWindowsInstaller = msi,
        ProductCode = productCode,
    };

    private static bool Exists(string path) => path is @"C:\Program Files\App\uninst.exe" or @"C:\Program Files\App\unins000.exe";

    [Theory]
    [InlineData("MsiExec.exe /I{23170F69-40C1-2702-2409-000001000000}")]
    [InlineData("MsiExec.exe /X{23170F69-40C1-2702-2409-000001000000}")]
    [InlineData("msiexec /x {23170F69-40C1-2702-2409-000001000000} /qb")]
    public void Msi_install_switch_becomes_uninstall(string uninstall)
    {
        var command = UninstallCommandBuilder.Build(P(uninstall, msi: true), silent: false, Exists)!;

        command.Kind.Should().Be(UninstallCommandKind.WindowsInstaller);
        command.FileName.Should().Be("msiexec.exe");
        command.Arguments.Should().Be("/x {23170f69-40c1-2702-2409-000001000000}");
        command.IsSilent.Should().BeFalse();
    }

    [Fact]
    public void Msi_silent_adds_qn_and_norestart()
    {
        var command = UninstallCommandBuilder.Build(P("MsiExec.exe /I{23170F69-40C1-2702-2409-000001000000}"), silent: true, Exists)!;

        command.Arguments.Should().Be("/x {23170f69-40c1-2702-2409-000001000000} /qn /norestart");
        command.IsSilent.Should().BeTrue();
    }

    [Fact]
    public void Msi_without_uninstall_string_uses_product_code() =>
        UninstallCommandBuilder.Build(P(null, msi: true, productCode: SevenZip), false, Exists)!
            .Arguments.Should().Be("/x {23170f69-40c1-2702-2409-000001000000}");

    [Fact]
    public void Quoted_exe_is_split()
    {
        var command = UninstallCommandBuilder.Build(P(@"""C:\Program Files\App\uninst.exe"" /remove"), false, Exists)!;

        command.Kind.Should().Be(UninstallCommandKind.Executable);
        command.FileName.Should().Be(@"C:\Program Files\App\uninst.exe");
        command.Arguments.Should().Be("/remove");
    }

    [Fact]
    public void Unquoted_exe_with_spaces_is_split_at_existing_file()
    {
        var command = UninstallCommandBuilder.Build(P(@"C:\Program Files\App\uninst.exe /S _?=C:\Program Files\App"), false, Exists)!;

        command.FileName.Should().Be(@"C:\Program Files\App\uninst.exe");
        command.Arguments.Should().Be(@"/S _?=C:\Program Files\App");
    }

    [Fact]
    public void Silent_prefers_quiet_uninstall_string()
    {
        var command = UninstallCommandBuilder.Build(
            P(@"""C:\Program Files\App\uninst.exe""", quiet: @"""C:\Program Files\App\uninst.exe"" /S"), silent: true, Exists)!;

        command.Arguments.Should().Be("/S");
        command.IsSilent.Should().BeTrue();
    }

    [Fact]
    public void Interactive_ignores_quiet_uninstall_string() =>
        UninstallCommandBuilder.Build(
            P(@"""C:\Program Files\App\uninst.exe""", quiet: @"""C:\Program Files\App\uninst.exe"" /S"), silent: false, Exists)!
            .Arguments.Should().BeEmpty();

    [Fact]
    public void Inno_setup_gets_documented_silent_switches()
    {
        var command = UninstallCommandBuilder.Build(P(@"""C:\Program Files\App\unins000.exe"""), silent: true, Exists)!;

        command.Arguments.Should().Be("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART");
        command.IsSilent.Should().BeTrue();
    }

    [Fact]
    public void Unknown_exe_in_silent_mode_is_reported_as_not_silent() =>
        UninstallCommandBuilder.Build(P(@"""C:\Program Files\App\uninst.exe"""), silent: true, Exists)!
            .IsSilent.Should().BeFalse();

    [Fact]
    public void Batch_file_runs_through_cmd()
    {
        var command = UninstallCommandBuilder.Build(P(@"C:\Tools\remove.bat"), false, Exists)!;

        command.Kind.Should().Be(UninstallCommandKind.Shell);
        command.FileName.Should().Be("cmd.exe");
        command.Arguments.Should().Be(@"/c ""C:\Tools\remove.bat""");
    }

    [Fact]
    public void Store_package_uses_full_name()
    {
        var command = UninstallCommandBuilder.Build(
            P(source: ProgramSource.Store, id: "Store:Microsoft.WindowsCalculator_11.2410.0.0_x64__8wekyb3d8bbwe"), false, Exists)!;

        command.Kind.Should().Be(UninstallCommandKind.StorePackage);
        command.FileName.Should().Be("Microsoft.WindowsCalculator_11.2410.0.0_x64__8wekyb3d8bbwe");
    }

    [Fact]
    public void Missing_uninstall_string_returns_null() =>
        UninstallCommandBuilder.Build(P(null), false, Exists).Should().BeNull();
}

public class UninstallEvaluatorTests
{
    private static ProcessRunResult Run(int? code = 0, bool cancelled = false) => new(code, false, cancelled, TimeSpan.FromSeconds(3));

    [Fact]
    public void Entry_gone_and_folder_empty_is_success() =>
        UninstallEvaluator.Evaluate(Run(), entryStillExists: false, installFolderHasFiles: false).Should().Be(UninstallOutcome.Success);

    [Fact]
    public void Entry_gone_but_files_left_is_partial() =>
        UninstallEvaluator.Evaluate(Run(), false, true).Should().Be(UninstallOutcome.Partial);

    [Fact]
    public void Entry_still_present_is_failure_even_with_exit_code_zero() =>
        UninstallEvaluator.Evaluate(Run(0), true, true).Should().Be(UninstallOutcome.Failed);

    [Fact]
    public void Msi_user_cancel_is_cancelled() =>
        UninstallEvaluator.Evaluate(Run(1602), true, true).Should().Be(UninstallOutcome.Cancelled);

    [Fact]
    public void Stop_button_is_cancelled() =>
        UninstallEvaluator.Evaluate(Run(null, cancelled: true), true, true).Should().Be(UninstallOutcome.Cancelled);

    [Fact]
    public void Entry_gone_wins_over_nonzero_exit_code() =>
        UninstallEvaluator.Evaluate(Run(1), false, false).Should().Be(UninstallOutcome.Success);

    [Theory]
    [InlineData(3010, true)]
    [InlineData(1641, true)]
    [InlineData(0, false)]
    [InlineData(null, false)]
    public void Restart_required_codes(int? code, bool expected) =>
        UninstallEvaluator.IsRestartRequired(code).Should().Be(expected);
}

public class UninstallRunnerTests
{
    private sealed class FakeRunner(ProcessRunResult result) : IProcessRunner
    {
        public UninstallCommand? LastCommand { get; private set; }

        public Task<ProcessRunResult> RunAsync(UninstallCommand command, TimeSpan timeout, CancellationToken cancellationToken)
        {
            LastCommand = command;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeStore : IStorePackageRemover
    {
        public string? Removed { get; private set; }

        public Task<ProcessRunResult> RemoveAsync(string packageFullName, CancellationToken cancellationToken)
        {
            Removed = packageFullName;
            return Task.FromResult(new ProcessRunResult(0, false, false, TimeSpan.Zero));
        }
    }

    private sealed class FakeProbe(bool entryExists, bool folderHasFiles) : IInstallStateProbe
    {
        public bool EntryExists(InstalledProgram program) => entryExists;

        public bool FolderHasFiles(string path) => folderHasFiles;

        public bool FileExists(string path) => true;
    }

    private static readonly InstallLocationRules Rules = new([@"C:\Program Files"]);

    private static InstalledProgram App(string? uninstall = @"""C:\Program Files\App\uninst.exe""") => new()
    {
        Id = "Registry64:App",
        DisplayName = "App",
        Source = ProgramSource.Registry64,
        UninstallString = uninstall,
        InstallLocation = @"C:\Program Files\App",
        RegistryKeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\App",
    };

    [Fact]
    public async Task Successful_uninstall()
    {
        var runner = new FakeRunner(new ProcessRunResult(0, false, false, TimeSpan.FromSeconds(4)));
        var sut = new UninstallRunner(runner, new FakeStore(), new FakeProbe(false, false), Rules);

        var result = await sut.RunAsync(App(), silent: false, CancellationToken.None);

        result.Outcome.Should().Be(UninstallOutcome.Success);
        runner.LastCommand!.FileName.Should().Be(@"C:\Program Files\App\uninst.exe");
    }

    [Fact]
    public async Task Leftover_files_make_partial()
    {
        var sut = new UninstallRunner(new FakeRunner(new ProcessRunResult(0, false, false, TimeSpan.Zero)), new FakeStore(), new FakeProbe(false, true), Rules);

        (await sut.RunAsync(App(), false, CancellationToken.None)).Outcome.Should().Be(UninstallOutcome.Partial);
    }

    [Fact]
    public async Task Broken_entry_reports_no_uninstaller()
    {
        var runner = new FakeRunner(new ProcessRunResult(0, false, false, TimeSpan.Zero));
        var sut = new UninstallRunner(runner, new FakeStore(), new FakeProbe(true, true), Rules);

        (await sut.RunAsync(App(uninstall: null), false, CancellationToken.None)).Outcome.Should().Be(UninstallOutcome.NoUninstaller);
        runner.LastCommand.Should().BeNull();
    }

    [Fact]
    public async Task Store_package_goes_to_store_remover()
    {
        var store = new FakeStore();
        var sut = new UninstallRunner(new FakeRunner(new ProcessRunResult(0, false, false, TimeSpan.Zero)), store, new FakeProbe(false, false), Rules);
        var program = new InstalledProgram { Id = "Store:Pkg_1.0.0.0_x64__abc", DisplayName = "Pkg", Source = ProgramSource.Store };

        (await sut.RunAsync(program, false, CancellationToken.None)).Outcome.Should().Be(UninstallOutcome.Success);
        store.Removed.Should().Be("Pkg_1.0.0.0_x64__abc");
    }
}

public class RunningProcessRulesTests
{
    private static readonly RunningProcess[] Processes =
    [
        new(10, "app", @"C:\Program Files\App\app.exe"),
        new(11, "helper", @"C:\Program Files\App\bin\helper.exe"),
        new(12, "other", @"C:\Program Files\AppOther\other.exe"),
        new(13, "svchost", @"C:\Program Files\App\svchost.exe"),
        new(14, "explorer", @"C:\Windows\explorer.exe"),
        new(15, "noaccess", null),
        new(99, "self", @"C:\Program Files\App\self.exe"),
    ];

    [Fact]
    public void Finds_processes_inside_install_folder_only() =>
        RunningProcessRules.FindProgramProcesses(Processes, @"C:\Program Files\App\", ownProcessId: 99)
            .Select(p => p.ProcessId).Should().Equal(10, 11);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\")]
    [InlineData("relatif")]
    public void Unusable_folder_matches_nothing(string? folder) =>
        RunningProcessRules.FindProgramProcesses(Processes, folder, 99).Should().BeEmpty();

    [Theory]
    [InlineData("csrss")]
    [InlineData("lsass.exe")]
    [InlineData("SVCHOST")]
    [InlineData("winlogon")]
    [InlineData("explorer")]
    public void Critical_processes_are_recognized(string name) =>
        RunningProcessRules.IsCritical(new RunningProcess(1, name, null)).Should().BeTrue();
}

public class ProgramMatcherTests
{
    private static InstalledProgram P(string name, string? location = null, string? icon = null, string? uninstall = null, bool system = false) => new()
    {
        Id = name,
        DisplayName = name,
        Source = ProgramSource.Registry64,
        InstallLocation = location,
        IconPath = icon,
        UninstallString = uninstall,
        IsSystemComponent = system,
    };

    private static readonly InstalledProgram[] Programs =
    [
        P("Google Chrome", location: @"C:\Program Files\Google\Chrome\Application"),
        P("Google Update", location: @"C:\Program Files\Google"),
        P("VLC", icon: @"C:\Program Files\VideoLAN\VLC\vlc.exe,0"),
        P("Notepad++", uninstall: @"""C:\Program Files\Notepad++\uninstall.exe"""),
        P("Shared", location: @"C:\Program Files"),
    ];

    [Theory]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "Google Chrome")]
    [InlineData(@"C:\Program Files\Google\Update\GoogleUpdate.exe", "Google Update")]
    [InlineData(@"C:\Program Files\VideoLAN\VLC\vlc.exe", "VLC")]
    [InlineData(@"C:\Program Files\Notepad++\notepad++.exe", "Notepad++")]
    public void Matches_by_most_specific_folder(string exe, string expected) =>
        ProgramMatcher.FindByExecutable(exe, Programs)!.DisplayName.Should().Be(expected);

    [Theory]
    [InlineData(@"C:\Program Files\Unknown\x.exe")]
    [InlineData(@"C:\Windows\System32\notepad.exe")]
    [InlineData(null)]
    public void No_match_for_unrelated_or_shared_folder(string? exe) =>
        ProgramMatcher.FindByExecutable(exe, Programs).Should().BeNull();

    [Fact]
    public void Visible_program_wins_over_system_component()
    {
        var list = new[]
        {
            P("Runtime", location: @"C:\Program Files\App", system: true),
            P("App", location: @"C:\Program Files\App"),
        };
        ProgramMatcher.FindByExecutable(@"C:\Program Files\App\app.exe", list)!.DisplayName.Should().Be("App");
    }

    [Theory]
    [InlineData("VLC", "VLC")]
    [InlineData("vlc", "VLC")]
    [InlineData("Google Chrome - 3 tab", "Google Chrome")]
    [InlineData("Tidak ada", null)]
    public void FindByName(string name, string? expected) =>
        ProgramMatcher.FindByName(name, Programs)?.DisplayName.Should().Be(expected);
}

/// <summary>Uji Job Object dengan proses dummy (cmd + ping ke localhost). Tidak menyentuh program apa pun.</summary>
[Trait("Category", "Integration")]
public class JobProcessRunnerTests
{
    private static UninstallCommand Cmd(string args) => new("cmd.exe", args, UninstallCommandKind.Shell, true);

    [Fact]
    public async Task Waits_for_child_processes_after_parent_exits()
    {
        // cmd langsung keluar, tetapi ping anaknya berjalan ~2 detik di latar.
        var runner = new JobProcessRunner(NullLogger<JobProcessRunner>.Instance);

        var result = await runner.RunAsync(Cmd("/c start \"\" /b ping -n 3 127.0.0.1 >nul"), TimeSpan.FromSeconds(30), CancellationToken.None);

        result.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(1.5));
        result.Cancelled.Should().BeFalse();
        result.TimedOut.Should().BeFalse();
    }

    [Fact]
    public async Task Returns_exit_code()
    {
        var runner = new JobProcessRunner(NullLogger<JobProcessRunner>.Instance);

        var result = await runner.RunAsync(Cmd("/c exit 7"), TimeSpan.FromSeconds(30), CancellationToken.None);

        result.ExitCode.Should().Be(7);
    }

    [Fact]
    public async Task Cancel_terminates_whole_tree()
    {
        var runner = new JobProcessRunner(NullLogger<JobProcessRunner>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        var result = await runner.RunAsync(Cmd("/c ping -n 30 127.0.0.1 >nul"), TimeSpan.FromSeconds(60), cts.Token);

        result.Cancelled.Should().BeTrue();
        result.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Timeout_stops_waiting()
    {
        var runner = new JobProcessRunner(NullLogger<JobProcessRunner>.Instance);

        var result = await runner.RunAsync(Cmd("/c ping -n 4 127.0.0.1 >nul"), TimeSpan.FromMilliseconds(500), CancellationToken.None);

        result.TimedOut.Should().BeTrue();
        result.ExitCode.Should().BeNull();
    }

    [Fact]
    public void Process_inspector_finds_own_process_path()
    {
        var inspector = new ProcessInspector(NullLogger<ProcessInspector>.Instance);
        var self = inspector.GetRunningProcesses().Single(p => p.ProcessId == Environment.ProcessId);

        self.ExecutablePath.Should().Be(Environment.ProcessPath);
        inspector.TryKill(self).Should().BeFalse("aplikasi tidak boleh menutup dirinya sendiri");
    }
}

using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MyUninstaller.App.ViewModels;
using MyUninstaller.App.Views;
using MyUninstaller.Core.Models;
using MyUninstaller.Core.Safety;
using MyUninstaller.Core.Scanning;
using MyUninstaller.Core.Uninstall;
using MyUninstaller.Infrastructure.Processes;
using MyUninstaller.Tests.Ui;

namespace MyUninstaller.Tests.Uninstall;

public class SilentCommandTests
{
    private static InstalledProgram P(string uninstall, string? quiet = null) => new()
    {
        Id = "Registry64:App",
        DisplayName = "App",
        Source = ProgramSource.Registry64,
        UninstallString = uninstall,
        QuietUninstallString = quiet,
    };

    private static bool Exists(string path) => true;

    [Fact]
    public void Nsis_detected_from_content_gets_slash_s()
    {
        var command = UninstallCommandBuilder.Build(P(@"""D:\Apps\App\uninstall.exe"""), silent: true, Exists, _ => InstallerKind.Nsis)!;

        command.Arguments.Should().Be("/S");
        command.IsSilent.Should().BeTrue();
    }

    [Fact]
    public void Nsis_slash_s_is_not_added_twice()
    {
        var command = UninstallCommandBuilder.Build(P(@"""D:\Apps\App\uninstall.exe"" /S"), silent: true, Exists, _ => InstallerKind.Nsis)!;

        command.Arguments.Should().Be("/S");
        command.IsSilent.Should().BeTrue();
    }

    [Fact]
    public void Renamed_inno_uninstaller_detected_from_content()
    {
        var command = UninstallCommandBuilder.Build(P(@"""D:\Apps\App\hapus.exe"""), silent: true, Exists, _ => InstallerKind.InnoSetup)!;

        command.Arguments.Should().Be("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART");
    }

    [Fact]
    public void Quiet_uninstall_string_wins_over_detection()
    {
        bool asked = false;
        var command = UninstallCommandBuilder.Build(P(@"""D:\Apps\App\uninstall.exe""", quiet: @"""D:\Apps\App\uninstall.exe"" /quiet"),
            silent: true, Exists, _ =>
            {
                asked = true;
                return InstallerKind.Nsis;
            })!;

        command.Arguments.Should().Be("/quiet");
        asked.Should().BeFalse();
    }

    [Fact]
    public void Interactive_mode_never_adds_silent_switches() =>
        UninstallCommandBuilder.Build(P(@"""D:\Apps\App\uninstall.exe"""), silent: false, Exists, _ => InstallerKind.Nsis)!
            .Arguments.Should().BeEmpty();

    [Fact]
    public void Unknown_uninstaller_stays_interactive() =>
        UninstallCommandBuilder.Build(P(@"""D:\Apps\App\uninstall.exe"""), silent: true, Exists, _ => InstallerKind.Unknown)!
            .IsSilent.Should().BeFalse();
}

public sealed class InstallerKindDetectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MyUninstallerTests", Guid.NewGuid().ToString("N"));

    private string Write(string name, string content)
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(content));
        return path;
    }

    [Theory]
    [InlineData("MZ....<assemblyIdentity name=\"Nullsoft.NSIS.exehead\"/>....", InstallerKind.Nsis)]
    [InlineData("MZ.........NullsoftInst....", InstallerKind.Nsis)]
    [InlineData("MZ....<assemblyIdentity name=\"JR.Inno.Setup\"/>....", InstallerKind.InnoSetup)]
    [InlineData("MZ....biasa saja....", InstallerKind.Unknown)]
    [InlineData("bukan PE Nullsoft.NSIS", InstallerKind.Unknown)]
    public void Detects_markers(string content, InstallerKind expected) =>
        new InstallerKindDetector().Detect(Write(Guid.NewGuid().ToString("N") + ".exe", content)).Should().Be(expected);

    [Fact]
    public void Missing_file_is_unknown() =>
        new InstallerKindDetector().Detect(Path.Combine(_dir, "tidak-ada.exe")).Should().Be(InstallerKind.Unknown);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}

public class ProgramRiskRulesTests
{
    private static InstalledProgram P(string name, string? uninstall = null, bool system = false) => new()
    {
        Id = name,
        DisplayName = name,
        Source = ProgramSource.Registry64,
        UninstallString = uninstall,
        IsSystemComponent = system,
    };

    // Nama-nama di bawah diambil dari daftar program nyata di laptop pengembang.
    [Theory]
    [InlineData("Windows Driver Package - ASUS (AsusPTPDrv) HIDClass  (04/21/2017 11.0.0.16)", ProgramRisk.Driver)]
    [InlineData("NVIDIA Graphics Driver 537.42", ProgramRisk.Driver)]
    [InlineData("Canon G2010 series MP Drivers", ProgramRisk.Driver)]
    [InlineData("ASUS PTP Driver", ProgramRisk.Driver)]
    [InlineData("Microsoft Visual C++ 2013 Redistributable (x64) - 12.0.30501", ProgramRisk.SharedRuntime)]
    [InlineData("Microsoft Visual C++ v14 Redistributable (x86) - 14.50.35719", ProgramRisk.SharedRuntime)]
    [InlineData("Microsoft .NET Runtime - 8.0.14 (x64)", ProgramRisk.SharedRuntime)]
    [InlineData("Microsoft .NET Core Runtime - 3.1.28 (x64)", ProgramRisk.SharedRuntime)]
    [InlineData("Microsoft ASP.NET Core 8.0.11 - Shared Framework (x64)", ProgramRisk.SharedRuntime)]
    [InlineData("Microsoft Edge WebView2 Runtime", ProgramRisk.SharedRuntime)]
    [InlineData("Microsoft Windows Desktop Runtime - 10.0.12 (x64)", ProgramRisk.SharedRuntime)]
    [InlineData("7-Zip 24.09 (x64)", ProgramRisk.None)]
    [InlineData("Google Chrome", ProgramRisk.None)]
    [InlineData("Git", ProgramRisk.None)]
    [InlineData("VLC media player", ProgramRisk.None)]
    public void Classify_by_name(string name, ProgramRisk expected) =>
        ProgramRiskRules.Classify(P(name)).Should().Be(expected);

    [Fact]
    public void Driver_uninstaller_command_is_recognized() =>
        ProgramRiskRules.Classify(P("Perangkat XYZ", @"C:\Windows\System32\DrvInst.exe /u C:\Windows\inf\oem12.inf")).Should().Be(ProgramRisk.Driver);

    [Fact]
    public void System_component_is_risky() =>
        ProgramRiskRules.Classify(P("Sesuatu", system: true)).Should().Be(ProgramRisk.SystemComponent);
}

public class SilentSessionTests
{
    private sealed class RecordingRunner : IProcessRunner
    {
        public List<UninstallCommand> Commands { get; } = [];

        public Task<ProcessRunResult> RunAsync(UninstallCommand command, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            return Task.FromResult(new ProcessRunResult(0, false, false, TimeSpan.Zero));
        }
    }

    private sealed class NoStore : IStorePackageRemover
    {
        public Task<ProcessRunResult> RemoveAsync(string packageFullName, CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessRunResult(0, false, false, TimeSpan.Zero));
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

    private sealed class NsisDetector : IInstallerKindDetector
    {
        public InstallerKind Detect(string executablePath) => InstallerKind.Nsis;
    }

    [StaTheory]
    [InlineData(true, "/S")]
    [InlineData(false, "")]
    public async Task Session_passes_silent_choice_to_runner(bool silent, string expectedArgs)
    {
        var runner = new RecordingRunner();
        var journal = new UninstallSessionTests.MemoryJournal();
        var services = new UninstallSessionServices(
            new UninstallRunner(runner, new NoStore(), new GoneProbe(), new InstallLocationRules([]), new NsisDetector()),
            new NoProcesses(),
            journal,
            new SafeRemover(new ProtectedPaths([], []), null!, null!, journal),
            _ => [],
            null);
        var programs = new[] { "Satu", "Dua" }.Select(n => new ProgramItemViewModel(new InstalledProgram
        {
            Id = n,
            DisplayName = n,
            Source = ProgramSource.Registry64,
            UninstallString = $@"""D:\Apps\{n}\uninstall.exe""",
        }, new DateOnly(2026, 11, 15))).ToList();

        var session = new UninstallSessionViewModel(programs, UninstallMode.Builtin, services, (_, _) => RunningProcessChoice.Ignore, () => true, NullLogger.Instance)
        {
            Silent = silent,
        };
        await session.RunAsync();

        runner.Commands.Should().HaveCount(2).And.OnlyContain(c => c.Arguments == expectedArgs);
        session.Items.Should().OnlyContain(i => i.Outcome == UninstallOutcome.Success);
    }

    [StaFact]
    public void Mode_dialog_checks_silent_for_bulk_and_disables_when_unsupported()
    {
        WindowXamlTests.EnsureApplicationResources();

        var bulk = new UninstallModeDialog("Hapus instalan 3 program", UninstallMode.Normal, silentCapable: 2, total: 3);
        bulk.SilentCheck.IsChecked.Should().BeTrue();
        bulk.SilentCheck.IsEnabled.Should().BeTrue();
        bulk.SilentCheck.Content.Should().Be("Mode senyap bila didukung (2 dari 3 program)");
        bulk.Close();

        var single = new UninstallModeDialog("Hapus instalan 7-Zip", UninstallMode.Normal, silentCapable: 1, total: 1);
        single.SilentCheck.IsChecked.Should().BeFalse("satu program: bawaan tidak senyap");
        single.Close();

        var none = new UninstallModeDialog("Hapus instalan Canon", UninstallMode.Normal, silentCapable: 0, total: 1);
        none.SilentCheck.IsEnabled.Should().BeFalse();
        none.SilentHint.Text.Should().Be("Mode senyap tidak tersedia untuk program ini.");
        none.Close();
    }
}

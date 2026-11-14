using MyUninstaller.Core.Models;
using MyUninstaller.Core.Scanning;
using MyUninstaller.Infrastructure.FileSystem;

namespace MyUninstaller.Tests.Scanning;

public class UninstallEntryMapperTests
{
    private static RawUninstallEntry Entry(
        string? name = "7-Zip 24.09",
        string keyName = "7-Zip",
        string? uninstall = @"""C:\Program Files\7-Zip\Uninstall.exe""",
        bool system = false,
        bool msi = false,
        string? parent = null,
        string? releaseType = null,
        long? sizeKb = null,
        string? installDate = null,
        DateOnly? lastWrite = null,
        string? location = null) => new()
    {
        KeyName = keyName,
        Source = ProgramSource.Registry64,
        RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + keyName,
        DisplayName = name,
        UninstallString = uninstall,
        SystemComponent = system,
        WindowsInstaller = msi,
        ParentKeyName = parent,
        ReleaseType = releaseType,
        EstimatedSizeKb = sizeKb,
        InstallDate = installDate,
        KeyLastWriteDate = lastWrite,
        InstallLocation = location,
    };

    [Fact]
    public void Normal_program_is_visible() =>
        UninstallEntryMapper.Map(Entry()).IsSystemComponent.Should().BeFalse();

    [Fact]
    public void SystemComponent_is_hidden() =>
        UninstallEntryMapper.Map(Entry(system: true)).IsSystemComponent.Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Entry_without_name_is_hidden_and_uses_key_name(string? name)
    {
        var program = UninstallEntryMapper.Map(Entry(name: name, keyName: "{ABC}"));

        program.IsSystemComponent.Should().BeTrue();
        program.DisplayName.Should().Be("{ABC}");
    }

    [Fact]
    public void Windows_update_with_parent_key_is_hidden() =>
        UninstallEntryMapper.Map(Entry(parent: "Microsoft Office")).IsSystemComponent.Should().BeTrue();

    [Theory]
    [InlineData("Security Update", true)]
    [InlineData("hotfix", true)]
    [InlineData("Update Rollup", true)]
    [InlineData("Product", false)]
    public void Release_type_updates_are_hidden(string releaseType, bool hidden) =>
        UninstallEntryMapper.Map(Entry(releaseType: releaseType)).IsSystemComponent.Should().Be(hidden);

    [Fact]
    public void Non_msi_entry_without_uninstaller_is_hidden() =>
        UninstallEntryMapper.Map(Entry(uninstall: null)).IsSystemComponent.Should().BeTrue();

    [Fact]
    public void Msi_entry_without_uninstall_string_is_visible() =>
        UninstallEntryMapper.Map(Entry(uninstall: null, msi: true)).IsSystemComponent.Should().BeFalse();

    [Fact]
    public void Estimated_size_is_converted_from_kilobytes()
    {
        UninstallEntryMapper.Map(Entry(sizeKb: 6_000)).SizeBytes.Should().Be(6_000L * 1024);
        UninstallEntryMapper.Map(Entry(sizeKb: 0)).SizeBytes.Should().BeNull();
        UninstallEntryMapper.Map(Entry(sizeKb: null)).SizeBytes.Should().BeNull();
    }

    [Fact]
    public void Install_date_falls_back_to_key_last_write()
    {
        var lastWrite = new DateOnly(2026, 3, 1);

        UninstallEntryMapper.Map(Entry(installDate: "20260902", lastWrite: lastWrite)).InstallDate.Should().Be(new DateOnly(2026, 9, 2));
        UninstallEntryMapper.Map(Entry(installDate: "rusak", lastWrite: lastWrite)).InstallDate.Should().Be(lastWrite);
        UninstallEntryMapper.Map(Entry(installDate: null, lastWrite: lastWrite)).InstallDate.Should().Be(lastWrite);
    }

    [Fact]
    public void Guid_key_becomes_product_code()
    {
        var program = UninstallEntryMapper.Map(Entry(keyName: "{23170F69-40C1-2702-2409-000001000000}", msi: true));

        program.ProductCode.Should().Be(Guid.Parse("23170F69-40C1-2702-2409-000001000000"));
        program.IsWindowsInstaller.Should().BeTrue();
        UninstallEntryMapper.Map(Entry(keyName: "7-Zip")).ProductCode.Should().BeNull();
    }

    [Theory]
    [InlineData(@"C:\Program Files\7-Zip\", @"C:\Program Files\7-Zip")]
    [InlineData(@"""C:\Program Files\7-Zip""", @"C:\Program Files\7-Zip")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData("  ", null)]
    public void Install_location_is_cleaned(string raw, string? expected) =>
        UninstallEntryMapper.Map(Entry(location: raw)).InstallLocation.Should().Be(expected);

    [Fact]
    public void Control_characters_and_spaces_are_removed_from_name() =>
        UninstallEntryMapper.Map(Entry(name: " 7-Zip\0 24.09\t ")).DisplayName.Should().Be("7-Zip 24.09");

    [Fact]
    public void Id_includes_source_so_same_key_in_two_hives_is_unique() =>
        UninstallEntryMapper.Map(Entry()).Id.Should().Be("Registry64:7-Zip");
}

public class IconLocationTests
{
    [Theory]
    [InlineData(@"C:\App\app.exe", @"C:\App\app.exe", 0)]
    [InlineData(@"C:\App\app.exe,0", @"C:\App\app.exe", 0)]
    [InlineData(@"C:\App\app.exe,-101", @"C:\App\app.exe", -101)]
    [InlineData(@"""C:\Program Files\App\app.exe"",2", @"C:\Program Files\App\app.exe", 2)]
    [InlineData(@"""C:\Program Files\App\app.exe""", @"C:\Program Files\App\app.exe", 0)]
    [InlineData(@"C:\Program Files\App, Inc\app.ico", @"C:\Program Files\App, Inc\app.ico", 0)]
    public void Parse_reads_path_and_index(string value, string path, int index) =>
        IconLocation.Parse(value).Should().Be(new IconLocation(path, index));

    [Fact]
    public void Parse_expands_environment_variables()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        IconLocation.Parse(@"%SystemRoot%\system32\shell32.dll,3")
            .Should().Be(new IconLocation(Path.Combine(windows, @"system32\shell32.dll"), 3));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@""""",0")]
    public void Parse_returns_null_for_empty(string? value) =>
        IconLocation.Parse(value).Should().BeNull();

    [Theory]
    [InlineData(@"""C:\Program Files\App\unins000.exe"" /SILENT", @"C:\Program Files\App\unins000.exe")]
    [InlineData(@"C:\Program Files\App\uninstall.exe /S", @"C:\Program Files\App\uninstall.exe")]
    [InlineData(@"MsiExec.exe /X{23170F69-40C1-2702-2409-000001000000}", null)]
    [InlineData(@"rundll32.exe dfshim.dll,ShArpMaintain app.application", null)]
    [InlineData(@"C:\App\uninstall.bat", null)]
    [InlineData(null, null)]
    public void ExecutableFromCommandLine(string? commandLine, string? expected) =>
        IconLocation.ExecutableFromCommandLine(commandLine).Should().Be(expected);
}

public class MsiGuidTests
{
    [Fact]
    public void ToPacked_office_example() =>
        MsiGuid.ToPacked(Guid.Parse("90120000-0011-0000-0000-0000000FF1CE")).Should().Be("00002109110000000000000000F01FEC");

    [Fact]
    public void ToPacked_7zip_example() =>
        MsiGuid.ToPacked(Guid.Parse("23170F69-40C1-2702-2409-000001000000")).Should().Be("96F071321C0420724290000010000000");
}

public class InstallLocationRulesTests
{
    private readonly InstallLocationRules _rules = new(
    [
        @"C:\Windows", @"C:\Program Files", @"C:\Program Files (x86)", @"C:\ProgramData", @"C:\Users",
    ]);

    [Theory]
    [InlineData(@"C:\Program Files\7-Zip", true)]
    [InlineData(@"C:\Program Files\7-Zip\", true)]
    [InlineData(@"D:\Games\Steam", true)]
    [InlineData(@"C:\Program Files", false)]
    [InlineData(@"c:\program files\", false)]
    [InlineData(@"C:\Windows", false)]
    [InlineData(@"C:\", false)]
    [InlineData(@"D:\", false)]
    [InlineData(@"\\server\share\app", false)]
    [InlineData(@"relatif\folder", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsMeasurable(string? path, bool expected) =>
        _rules.IsMeasurable(path).Should().Be(expected);

    [Fact]
    public void Current_system_rules_reject_windows_and_program_files()
    {
        var rules = InstallLocationRules.ForCurrentSystem();

        rules.IsMeasurable(Environment.GetFolderPath(Environment.SpecialFolder.Windows)).Should().BeFalse();
        rules.IsMeasurable(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)).Should().BeFalse();
        rules.IsMeasurable(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).Should().BeFalse();
        rules.IsMeasurable(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip")).Should().BeTrue();
    }
}

public class ProgramDeduplicatorTests
{
    private static InstalledProgram P(string name, ProgramSource source, bool system = false, string? version = "1.0") => new()
    {
        Id = $"{source}:{name}",
        DisplayName = name,
        Source = source,
        Version = version,
        IsSystemComponent = system,
    };

    [Fact]
    public void Identical_entries_keep_preferred_source()
    {
        var result = ProgramDeduplicator.Deduplicate(
        [
            P("Zoom", ProgramSource.CurrentUser),
            P("Zoom", ProgramSource.Registry64),
        ]);

        result.Should().ContainSingle().Which.Source.Should().Be(ProgramSource.Registry64);
    }

    [Fact]
    public void Visible_entry_wins_over_hidden_duplicate()
    {
        var result = ProgramDeduplicator.Deduplicate(
        [
            P("App", ProgramSource.Registry64, system: true),
            P("App", ProgramSource.Registry32),
        ]);

        result.Should().ContainSingle().Which.IsSystemComponent.Should().BeFalse();
    }

    [Fact]
    public void Different_versions_are_kept() =>
        ProgramDeduplicator.Deduplicate(
        [
            P("Python", ProgramSource.Registry64, version: "3.12"),
            P("Python", ProgramSource.Registry64, version: "3.13"),
        ]).Should().HaveCount(2);
}

public sealed class FolderSizeCalculatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MyUninstallerTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Sums_files_recursively()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "sub", "dalam"));
        File.WriteAllBytes(Path.Combine(_dir, "a.bin"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(_dir, "sub", "b.bin"), new byte[234]);
        File.WriteAllBytes(Path.Combine(_dir, "sub", "dalam", "c.bin"), new byte[66]);

        new FolderSizeCalculator().Measure(_dir).Should().Be(1300);
    }

    [Fact]
    public void Empty_folder_is_zero()
    {
        Directory.CreateDirectory(_dir);
        new FolderSizeCalculator().Measure(_dir).Should().Be(0);
    }

    [Fact]
    public void Missing_folder_is_null() =>
        new FolderSizeCalculator().Measure(Path.Combine(_dir, "tidak-ada")).Should().BeNull();

    [Fact]
    public void Honors_cancellation()
    {
        Directory.CreateDirectory(_dir);
        for (int i = 0; i < 1100; i++)
        {
            File.WriteAllBytes(Path.Combine(_dir, $"{i}.bin"), [1]);
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        FluentActions.Invoking(() => new FolderSizeCalculator().Measure(_dir, cts.Token))
            .Should().Throw<OperationCanceledException>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}

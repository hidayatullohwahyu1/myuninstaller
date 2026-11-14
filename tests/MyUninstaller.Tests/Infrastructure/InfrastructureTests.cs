using MyUninstaller.Core.Settings;
using MyUninstaller.Infrastructure.Settings;
using MyUninstaller.Infrastructure.Theming;

namespace MyUninstaller.Tests.Infrastructure;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MyUninstallerTests", Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_dir, "pengaturan.json");

    [Fact]
    public void Load_returns_defaults_when_file_missing()
    {
        var settings = new JsonSettingsStore(FilePath).Load();

        settings.Should().Be(new AppSettings());
        settings.CreateRestorePoint.Should().BeTrue();
        settings.QuarantineRetentionDays.Should().Be(30);
        settings.ViewMode.Should().Be(ProgramViewMode.Icons);
    }

    [Fact]
    public void Save_then_load_round_trips()
    {
        var store = new JsonSettingsStore(FilePath);
        var saved = new AppSettings
        {
            ViewMode = ProgramViewMode.Details,
            SortKey = ProgramSortKey.Size,
            SortDescending = true,
            MainWindow = new WindowPlacement(10, 20, 1200, 800, IsMaximized: true),
        };

        store.Save(saved);

        store.Load().Should().Be(saved);
        File.Exists(FilePath + ".tmp").Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ rusak")]
    [InlineData("null")]
    [InlineData("{\"ViewMode\": \"TidakAda\"}")]
    public void Load_returns_defaults_when_file_is_corrupt(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, content);

        new JsonSettingsStore(FilePath).Load().Should().Be(new AppSettings());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}

public class WindowsThemeReaderTests
{
    [Theory]
    [InlineData(0xFFD77800u, 0xFF0078D7u)] // biru Windows bawaan
    [InlineData(0x00112233u, 0xFF332211u)]
    public void AbgrToArgb_swaps_red_and_blue(uint abgr, uint argb) =>
        WindowsThemeReader.AbgrToArgb(abgr).Should().Be(argb);
}

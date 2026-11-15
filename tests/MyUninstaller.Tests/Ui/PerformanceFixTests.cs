using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using MyUninstaller.App.Controls;
using MyUninstaller.Core.Abstractions;
using MyUninstaller.Infrastructure.FileSystem;
using MyUninstaller.Tests.Safety;

namespace MyUninstaller.Tests.Ui;

public class FolderSizeCacheTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "MyUninstallerTests", Guid.NewGuid().ToString("N"));

    private sealed class CountingCalculator : IFolderSizeCalculator
    {
        public int Calls { get; private set; }

        public long? Measure(string path, CancellationToken cancellationToken = default)
        {
            Calls++;
            return 1234;
        }
    }

    [Fact]
    public void Rules()
    {
        var time = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var entry = new FolderSizeEntry(10, time, time);

        FolderSizeCacheRules.IsValid(entry, time, time.AddDays(6)).Should().BeTrue();
        FolderSizeCacheRules.IsValid(entry, time, time.AddDays(8)).Should().BeFalse("lebih dari 7 hari");
        FolderSizeCacheRules.IsValid(entry, time.AddSeconds(1), time.AddDays(1)).Should().BeFalse("folder berubah");
        FolderSizeCacheRules.IsValid(entry, time, time.AddDays(-1)).Should().BeFalse("jam mundur");
    }

    [Fact]
    public void Measured_once_across_sessions_and_remeasured_when_folder_changes()
    {
        string folder = Path.Combine(_work, "App");
        Directory.CreateDirectory(folder);
        string cacheFile = Path.Combine(_work, "ukuran-folder.json");
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var inner = new CountingCalculator();

        var first = new CachedFolderSizes(inner, clock, cacheFile, NullLogger<CachedFolderSizes>.Instance);
        first.Measure(folder).Should().Be(1234);
        first.Measure(folder + @"\").Should().Be(1234);
        first.Save();
        inner.Calls.Should().Be(1);

        // Sesi berikutnya: dari berkas cache, tidak dihitung ulang.
        var second = new CachedFolderSizes(inner, clock, cacheFile, NullLogger<CachedFolderSizes>.Instance);
        second.Measure(folder).Should().Be(1234);
        inner.Calls.Should().Be(1);

        // Isi folder berubah → dihitung ulang.
        Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow.AddMinutes(5));
        second.Measure(folder);
        inner.Calls.Should().Be(2);

        File.WriteAllText(cacheFile, "{ rusak");
        new CachedFolderSizes(inner, clock, cacheFile, NullLogger<CachedFolderSizes>.Instance).Measure(folder).Should().Be(1234);
    }

    public void Dispose()
    {
        if (Directory.Exists(_work))
        {
            Directory.Delete(_work, recursive: true);
        }
    }
}

[Collection(WpfCollection.Name)]
public class VirtualizingTilePanelTests
{
    [StaFact]
    public void Only_visible_tiles_are_created()
    {
        WindowXamlTests.EnsureApplicationResources();
        var factory = new FrameworkElementFactory(typeof(VirtualizingTilePanel));
        var list = new ListBox
        {
            ItemsSource = Enumerable.Range(0, 500).Select(i => $"Program {i}").ToList(),
            ItemsPanel = new ItemsPanelTemplate(factory),
            Width = 600,
            Height = 400,
        };
        ScrollViewer.SetCanContentScroll(list, true);
        var window = new Window { Content = list, Left = -20000, Top = -20000, ShowActivated = false, Width = 640, Height = 460 };
        window.Show();
        list.UpdateLayout();

        int Realized() => Enumerable.Range(0, 500).Count(i => list.ItemContainerGenerator.ContainerFromIndex(i) is not null);

        // ±5 kolom × (4 baris terlihat + cadangan) — jauh di bawah 500.
        Realized().Should().BeInRange(10, 40);

        list.ScrollIntoView("Program 450");
        list.UpdateLayout();
        list.ItemContainerGenerator.ContainerFromIndex(450).Should().NotBeNull("ScrollIntoView membuat ubin tujuan");
        list.ItemContainerGenerator.ContainerFromIndex(0).Should().BeNull("ubin di atas dibuang");
        Realized().Should().BeLessThan(40);

        window.Close();
    }
}

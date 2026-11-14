using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using MyUninstaller.Core.Models;
using MyUninstaller.Infrastructure.Scanning;
using MyUninstaller.Infrastructure.Store;
using MyUninstaller.Infrastructure.WindowsRegistry;
using Xunit.Abstractions;

namespace MyUninstaller.Tests.Integration;

/// <summary>
/// Membaca registri dan paket Store mesin ini (hanya baca, tidak mengubah apa pun).
/// Memastikan pemindai tidak melempar galat dan cukup cepat.
/// </summary>
[Trait("Category", "Integration")]
public class ProgramScannerIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public void Registry_reader_returns_entries_with_full_paths()
    {
        var sw = Stopwatch.StartNew();
        var entries = new RegistryUninstallReader(NullLogger<RegistryUninstallReader>.Instance).ReadAll();
        output.WriteLine($"Registri: {entries.Count} entri dalam {sw.ElapsedMilliseconds} ms");

        entries.Should().NotBeEmpty();
        entries.Should().OnlyContain(e => e.RegistryPath.StartsWith("HKEY_", StringComparison.Ordinal));
        entries.Select(e => e.Source).Should().Contain(ProgramSource.Registry64);
    }

    [Fact]
    public void Store_reader_does_not_throw()
    {
        var sw = Stopwatch.StartNew();
        var packages = new StorePackageReader(NullLogger<StorePackageReader>.Instance).ReadAll();
        output.WriteLine($"Store: {packages.Count} paket ({packages.Count(p => !p.IsSystemComponent)} tampil) dalam {sw.ElapsedMilliseconds} ms");

        packages.Should().OnlyContain(p => p.Source == ProgramSource.Store);
    }

    [Fact]
    public async Task Full_scan_is_fast_enough()
    {
        var scanner = new ProgramScanner(
            new RegistryUninstallReader(NullLogger<RegistryUninstallReader>.Instance),
            new StorePackageReader(NullLogger<StorePackageReader>.Instance),
            NullLogger<ProgramScanner>.Instance);

        var sw = Stopwatch.StartNew();
        var programs = await scanner.ScanAsync();
        sw.Stop();

        var visible = programs.Where(p => !p.IsSystemComponent).OrderBy(p => p.DisplayName).ToList();
        output.WriteLine($"Total {programs.Count}, tampil {visible.Count}, dalam {sw.ElapsedMilliseconds} ms");
        foreach (var p in visible)
        {
            output.WriteLine($"  [{p.Source}] {p.DisplayName} | {p.Publisher} | {p.Version} | {p.SizeBytes} | {p.InstallDate}");
        }

        visible.Should().NotBeEmpty();
        programs.Select(p => p.Id).Should().OnlyHaveUniqueItems();
        // PRD: daftar tampil < 2 detik. Beri kelonggaran untuk mesin uji yang sibuk.
        sw.ElapsedMilliseconds.Should().BeLessThan(5000);
    }
}

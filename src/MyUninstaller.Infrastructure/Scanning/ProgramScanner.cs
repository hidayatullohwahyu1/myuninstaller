using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MyUninstaller.Core.Abstractions;
using MyUninstaller.Core.Models;
using MyUninstaller.Core.Scanning;
using MyUninstaller.Infrastructure.WindowsRegistry;
using MyUninstaller.Infrastructure.Store;

namespace MyUninstaller.Infrastructure.Scanning;

/// <summary>
/// Pemindai program: registri Uninstall (HKLM 64/32-bit, HKCU) dan paket Store dibaca bersamaan,
/// lalu dipetakan dan dibuang yang ganda. Hasil berisi juga komponen sistem (ditandai, disaring di UI).
/// </summary>
public sealed class ProgramScanner(
    IRegistryUninstallReader registryReader,
    IStorePackageReader storeReader,
    ILogger<ProgramScanner> logger) : IProgramScanner
{
    public async Task<IReadOnlyList<InstalledProgram>> ScanAsync(
        IProgress<IReadOnlyList<InstalledProgram>>? partialResults = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        var registryTask = Task.Run(
            () => registryReader.ReadAll(cancellationToken).Select(UninstallEntryMapper.Map).ToList(),
            cancellationToken);

        // Kegagalan Store tidak boleh menghilangkan daftar program Win32.
        var storeTask = Task.Run(() =>
        {
            try
            {
                return storeReader.ReadAll(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Pemindaian paket Store gagal");
                return [];
            }
        }, cancellationToken);

        // Registri biasanya selesai jauh lebih dulu; tampilkan dulu sambil menunggu Store.
        await registryTask.ConfigureAwait(false);
        if (partialResults is not null && !storeTask.IsCompleted)
        {
            partialResults.Report(ProgramDeduplicator.Deduplicate(registryTask.Result));
        }

        await storeTask.ConfigureAwait(false);

        var registry = registryTask.Result;
        var store = storeTask.Result;
        var programs = ProgramDeduplicator.Deduplicate(registry.Concat(store));

        logger.LogInformation(
            "Pemindaian selesai dalam {Elapsed} ms: {Registry} entri registri, {Store} paket Store, {Visible} tampil, {Hidden} komponen sistem",
            stopwatch.ElapsedMilliseconds, registry.Count, store.Count,
            programs.Count(p => !p.IsSystemComponent), programs.Count(p => p.IsSystemComponent));

        return programs;
    }
}

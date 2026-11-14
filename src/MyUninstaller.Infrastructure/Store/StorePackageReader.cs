using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MyUninstaller.Core.Models;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace MyUninstaller.Infrastructure.Store;

/// <summary>Membaca aplikasi Microsoft Store/UWP pengguna saat ini (hanya baca).</summary>
public interface IStorePackageReader
{
    IReadOnlyList<InstalledProgram> ReadAll(CancellationToken cancellationToken = default);
}

public sealed class StorePackageReader(ILogger<StorePackageReader> logger) : IStorePackageReader
{
    public IReadOnlyList<InstalledProgram> ReadAll(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Package> packages;
        try
        {
            var manager = new PackageManager();
            // String kosong = pengguna saat ini. Hanya paket utama (tanpa framework, resource, bundle).
            packages = [.. manager.FindPackagesForUserWithPackageTypes(string.Empty, PackageTypes.Main)];
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Daftar paket Store tidak bisa dibaca");
            return [];
        }

        // Beberapa properti Package lambat (DisplayName, GetAppListEntries, Logo: puluhan ms per paket),
        // jadi paket diproses paralel. Objek Package bersifat agile, aman dipakai dari banyak utas.
        var mapped = new InstalledProgram?[packages.Count];
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8),
        };
        Parallel.For(0, packages.Count, options, i =>
        {
            try
            {
                mapped[i] = Map(packages[i]);
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or FileNotFoundException)
            {
                logger.LogDebug(ex, "Paket Store dilewati");
            }
        });

        return [.. mapped.OfType<InstalledProgram>()];
    }

    private static InstalledProgram? Map(Package package)
    {
        if (package.IsFramework || package.IsResourcePackage || package.IsBundle)
        {
            return null;
        }

        PackageId id = package.Id;
        string? displayName = Safe(() => package.DisplayName);
        bool nameUnresolved = string.IsNullOrWhiteSpace(displayName) || displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase);

        // Paket tanpa entri di menu Mulai (layanan latar, ekstensi) diperlakukan sebagai komponen sistem,
        // sama seperti paket bertanda tangan System (aplikasi bawaan yang tidak bisa dihapus).
        bool isSystem = package.SignatureKind == PackageSignatureKind.System
                        || nameUnresolved
                        || CountAppListEntries(package) == 0;

        PackageVersion v = id.Version;
        string? installedPath = GetInstalledPath(package);

        return new InstalledProgram
        {
            Id = "Store:" + id.FullName,
            DisplayName = nameUnresolved ? id.Name : displayName!.Trim(),
            Source = ProgramSource.Store,
            Publisher = Safe(() => package.PublisherDisplayName) is { Length: > 0 } publisher && !publisher.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)
                ? publisher.Trim()
                : null,
            Version = $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}",
            InstallDate = GetInstallDate(installedPath),
            InstallLocation = installedPath,
            IconPath = Safe(() => package.Logo?.LocalPath),
            IsSystemComponent = isSystem,
        };
    }

    /// <summary>
    /// Tanggal pasang dari waktu pembuatan folder paket (folder berversi dibuat saat dipasang/diperbarui).
    /// Jauh lebih cepat daripada Package.InstalledDate.
    /// </summary>
    private static DateOnly? GetInstallDate(string? installedPath)
    {
        if (string.IsNullOrEmpty(installedPath))
        {
            return null;
        }

        try
        {
            DateTime created = Directory.GetCreationTime(installedPath);
            return created.Year > 2000 ? DateOnly.FromDateTime(created) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Jumlah entri menu Mulai. Windows 10 1809–1909 tidak punya API ini: anggap ada.</summary>
    private static int CountAppListEntries(Package package)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            return 1;
        }

        try
        {
            return package.GetAppListEntries().Count;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            return 0;
        }
    }

    private static string? GetInstalledPath(Package package)
    {
        try
        {
            return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
                ? package.InstalledPath
                : package.InstalledLocation?.Path;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException or FileNotFoundException)
        {
            return null;
        }
    }

    private static T? Safe<T>(Func<T> getter)
    {
        try
        {
            return getter();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or FileNotFoundException or UriFormatException)
        {
            return default;
        }
    }
}

using System.Security;
using Microsoft.Win32;
using MyUninstaller.Core.Models;
using MyUninstaller.Core.Scanning;

namespace MyUninstaller.Infrastructure.Scanning;

/// <summary>Mencari berkas ikon terbaik untuk sebuah program.</summary>
public interface IIconPathResolver
{
    IconLocation? Resolve(InstalledProgram program);
}

/// <summary>
/// Urutan: DisplayIcon → ProductIcon MSI → exe dari UninstallString. Untuk Store: logo paket
/// (bila berkas persisnya tidak ada, pilih varian skala terbesar). Path jaringan dilewati agar tidak macet.
/// </summary>
public sealed class IconPathResolver : IIconPathResolver
{
    public IconLocation? Resolve(InstalledProgram program)
    {
        try
        {
            if (program.Source == ProgramSource.Store)
            {
                return ResolveStoreLogo(program.IconPath);
            }

            if (Usable(IconLocation.Parse(program.IconPath)) is { } displayIcon)
            {
                return displayIcon;
            }

            if (program.ProductCode is { } productCode && Usable(IconLocation.Parse(ReadMsiProductIcon(productCode))) is { } msiIcon)
            {
                return msiIcon;
            }

            if (IconLocation.ExecutableFromCommandLine(program.UninstallString) is { } exe
                && Usable(new IconLocation(exe, 0)) is { } uninstallerIcon)
            {
                return uninstallerIcon;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or SecurityException or NotSupportedException)
        {
            // Ikon rusak → pakai ikon pengganti.
        }

        return null;
    }

    private static IconLocation? Usable(IconLocation? location) =>
        location is not null
        && !location.Path.StartsWith(@"\\", StringComparison.Ordinal)
        && Path.IsPathFullyQualified(location.Path)
        && location.HasIconExtension
        && File.Exists(location.Path)
            ? location
            : null;

    private static string? ReadMsiProductIcon(Guid productCode)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey($@"SOFTWARE\Classes\Installer\Products\{MsiGuid.ToPacked(productCode)}", writable: false);
        return key?.GetValue("ProductIcon") as string;
    }

    private static IconLocation? ResolveStoreLogo(string? logoPath)
    {
        if (string.IsNullOrWhiteSpace(logoPath) || logoPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return null;
        }

        if (File.Exists(logoPath))
        {
            return new IconLocation(logoPath, 0);
        }

        // Manifest menyebut "StoreLogo.png", berkas sebenarnya "StoreLogo.scale-200.png" dsb.
        string? directory = Path.GetDirectoryName(logoPath);
        if (directory is null || !Directory.Exists(directory))
        {
            return null;
        }

        string baseName = Path.GetFileNameWithoutExtension(logoPath);
        string extension = Path.GetExtension(logoPath);
        var best = new DirectoryInfo(directory)
            .EnumerateFiles($"{baseName}*{extension}")
            .Where(f => !f.Name.Contains("contrast-", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Length)
            .FirstOrDefault();

        return best is null ? null : new IconLocation(best.FullName, 0);
    }
}

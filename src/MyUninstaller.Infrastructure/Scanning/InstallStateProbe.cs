using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;
using MyUninstaller.Core.Models;
using MyUninstaller.Core.Uninstall;
using Windows.Management.Deployment;

namespace MyUninstaller.Infrastructure.Scanning;

/// <summary>Pemeriksaan kondisi instalasi (hanya baca).</summary>
public sealed class InstallStateProbe : IInstallStateProbe
{
    public bool EntryExists(InstalledProgram program)
    {
        if (program.Source == ProgramSource.Store)
        {
            return StorePackageExists(program.Id.StartsWith("Store:", StringComparison.Ordinal) ? program.Id["Store:".Length..] : program.Id);
        }

        return program.RegistryKeyPath is { } path && RegistryKeyExists(path);
    }

    public bool FolderHasFiles(string path)
    {
        try
        {
            return Directory.Exists(path)
                && Directory.EnumerateFiles(path, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                }).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // Tidak bisa dipastikan kosong: anggap masih ada isinya.
        }
    }

    public bool FileExists(string path) => File.Exists(path);

    /// <summary>
    /// Membuka path seperti HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\... dengan view 64-bit
    /// (WOW6432Node ditulis eksplisit, jadi tidak ada pengalihan).
    /// </summary>
    internal static bool RegistryKeyExists(string fullPath)
    {
        int slash = fullPath.IndexOf('\\');
        if (slash < 0)
        {
            return false;
        }

        RegistryHive? hive = fullPath[..slash].ToUpperInvariant() switch
        {
            "HKEY_LOCAL_MACHINE" or "HKLM" => RegistryHive.LocalMachine,
            "HKEY_CURRENT_USER" or "HKCU" => RegistryHive.CurrentUser,
            "HKEY_USERS" or "HKU" => RegistryHive.Users,
            _ => null,
        };
        if (hive is null)
        {
            return false;
        }

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive.Value, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(fullPath[(slash + 1)..], writable: false);
            return key is not null;
        }
        catch (SecurityException)
        {
            return true; // Ada tetapi tidak boleh dibaca.
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool StorePackageExists(string packageFullName)
    {
        try
        {
            return new PackageManager().FindPackageForUser(string.Empty, packageFullName) is not null;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

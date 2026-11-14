using System.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using MyUninstaller.Core.Models;
using MyUninstaller.Core.Scanning;

namespace MyUninstaller.Infrastructure.WindowsRegistry;

/// <summary>Membaca semua subkunci registri Uninstall (hanya baca).</summary>
public interface IRegistryUninstallReader
{
    IReadOnlyList<RawUninstallEntry> ReadAll(CancellationToken cancellationToken = default);
}

public sealed class RegistryUninstallReader(ILogger<RegistryUninstallReader> logger) : IRegistryUninstallReader
{
    private const string UninstallSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public IReadOnlyList<RawUninstallEntry> ReadAll(CancellationToken cancellationToken = default)
    {
        var entries = new List<RawUninstallEntry>(256);

        // View dibaca eksplisit agar tidak tertukar oleh pengalihan WOW64.
        ReadLocation(RegistryHive.LocalMachine, RegistryView.Registry64, ProgramSource.Registry64,
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", entries, cancellationToken);

        if (Environment.Is64BitOperatingSystem)
        {
            ReadLocation(RegistryHive.LocalMachine, RegistryView.Registry32, ProgramSource.Registry32,
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", entries, cancellationToken);
        }

        ReadLocation(RegistryHive.CurrentUser, RegistryView.Registry64, ProgramSource.CurrentUser,
            @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", entries, cancellationToken);

        return entries;
    }

    private void ReadLocation(
        RegistryHive hive,
        RegistryView view,
        ProgramSource source,
        string displayPath,
        List<RawUninstallEntry> entries,
        CancellationToken cancellationToken)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(UninstallSubKey, writable: false);
            if (uninstall is null)
            {
                return;
            }

            foreach (string keyName in uninstall.GetSubKeyNames())
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Setiap entri dibungkus sendiri: satu kunci rusak tidak menghentikan pemindaian.
                try
                {
                    using var key = uninstall.OpenSubKey(keyName, writable: false);
                    if (key is not null)
                    {
                        entries.Add(ReadEntry(key, keyName, source, $@"{displayPath}\{keyName}"));
                    }
                }
                catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
                {
                    logger.LogDebug(ex, "Kunci Uninstall dilewati: {Path}", $@"{displayPath}\{keyName}");
                }
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            logger.LogWarning(ex, "Lokasi Uninstall tidak bisa dibaca: {Path}", displayPath);
        }
    }

    private static RawUninstallEntry ReadEntry(RegistryKey key, string keyName, ProgramSource source, string path) => new()
    {
        KeyName = keyName,
        Source = source,
        RegistryPath = path,
        DisplayName = RegistryValues.GetString(key, "DisplayName"),
        DisplayVersion = RegistryValues.GetString(key, "DisplayVersion"),
        Publisher = RegistryValues.GetString(key, "Publisher"),
        InstallDate = RegistryValues.GetString(key, "InstallDate"),
        InstallLocation = RegistryValues.GetString(key, "InstallLocation"),
        DisplayIcon = RegistryValues.GetString(key, "DisplayIcon"),
        UninstallString = RegistryValues.GetString(key, "UninstallString"),
        QuietUninstallString = RegistryValues.GetString(key, "QuietUninstallString"),
        ParentKeyName = RegistryValues.GetString(key, "ParentKeyName"),
        ReleaseType = RegistryValues.GetString(key, "ReleaseType"),
        HelpLink = RegistryValues.GetString(key, "HelpLink"),
        UrlInfoAbout = RegistryValues.GetString(key, "URLInfoAbout"),
        EstimatedSizeKb = RegistryValues.GetInt64(key, "EstimatedSize"),
        SystemComponent = RegistryValues.GetFlag(key, "SystemComponent"),
        WindowsInstaller = RegistryValues.GetFlag(key, "WindowsInstaller"),
        KeyLastWriteDate = RegistryValues.GetLastWriteTimeUtc(key) is { } utc
            ? DateOnly.FromDateTime(utc.ToLocalTime())
            : null,
    };
}

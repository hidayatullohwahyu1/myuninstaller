using System.Diagnostics;
using System.Globalization;
using System.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using MyUninstaller.Core.Safety;
using MyUninstaller.Infrastructure.Scanning;

namespace MyUninstaller.Infrastructure.Safety;

/// <summary>
/// Cadangan registri memakai reg.exe bawaan Windows (format .reg standar, bisa diimpor manual dengan
/// klik ganda). Path dengan WOW6432Node ditulis eksplisit dan dibuka dengan view 64-bit.
/// </summary>
public sealed class RegistryBackupService(string backupRoot, ILogger<RegistryBackupService> logger) : IRegistryBackupService
{
    private static readonly TimeSpan RegTimeout = TimeSpan.FromSeconds(60);
    private readonly Lock _gate = new();

    public RegistryBackupService(AppPaths paths, ILogger<RegistryBackupService> logger)
        : this(paths.QuarantineDirectory, logger)
    {
    }

    public bool KeyExists(string keyPath) => InstallStateProbe.RegistryKeyExists(keyPath);

    public string? Export(string sessionId, string keyPath)
    {
        string folder = Path.Combine(backupRoot, sessionId, "registri");
        string file;
        lock (_gate)
        {
            Directory.CreateDirectory(folder);
            int index = 1;
            while (File.Exists(Path.Combine(folder, $"{index:D3}.reg")))
            {
                index++;
            }

            file = Path.Combine(folder, $"{index:D3}.reg");
        }

        bool ok = RunReg($"export \"{ToRegExePath(keyPath)}\" \"{file}\" /y /reg:64", out string output);
        if (!ok || !File.Exists(file) || new FileInfo(file).Length == 0)
        {
            logger.LogWarning("Ekspor .reg gagal untuk {Key}: {Output}", keyPath, output);
            return null;
        }

        logger.LogInformation("Cadangan registri {Key} → {File}", keyPath, file);
        return file;
    }

    public bool DeleteKey(string keyPath)
    {
        if (ProtectedRegistryKeys.IsProtected(keyPath))
        {
            logger.LogWarning("Menolak menghapus kunci terlindung {Key}", keyPath);
            return false;
        }

        var (hive, subKey) = Split(keyPath);
        if (hive is null || string.IsNullOrEmpty(subKey))
        {
            return false;
        }

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive.Value, RegistryView.Registry64);
            baseKey.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
            logger.LogInformation("Kunci registri dihapus: {Key}", keyPath);
            return true;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            // Akses ditolak: lewati. Tidak mengambil alih kepemilikan (PRD).
            logger.LogWarning(ex, "Kunci registri tidak bisa dihapus: {Key}", keyPath);
            return false;
        }
    }

    public bool Import(string regFile)
    {
        if (!File.Exists(regFile))
        {
            return false;
        }

        bool ok = RunReg($"import \"{regFile}\" /reg:64", out string output);
        if (!ok)
        {
            logger.LogWarning("Impor .reg gagal {File}: {Output}", regFile, output);
        }

        return ok;
    }

    public bool ValueExists(string keyPath, string valueName)
    {
        var (hive, subKey) = Split(keyPath);
        if (hive is null)
        {
            return false;
        }

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive.Value, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(subKey, writable: false);
            return key?.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase) == true;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public bool DeleteValue(string keyPath, string valueName)
    {
        if (ProtectedRegistryKeys.IsProtectedValue(keyPath, valueName))
        {
            logger.LogWarning("Menolak menghapus nilai terlindung {Key}\\{Value}", keyPath, valueName);
            return false;
        }

        var (hive, subKey) = Split(keyPath);
        if (hive is null)
        {
            return false;
        }

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive.Value, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(subKey, writable: true);
            if (key is null)
            {
                return false;
            }

            key.DeleteValue(valueName, throwOnMissingValue: false);
            logger.LogInformation("Nilai registri dihapus: {Key}\\{Value}", keyPath, valueName);
            return true;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            logger.LogWarning(ex, "Nilai registri tidak bisa dihapus: {Key}\\{Value}", keyPath, valueName);
            return false;
        }
    }

    /// <summary>reg.exe memakai singkatan HKLM/HKCU/HKU.</summary>
    internal static string ToRegExePath(string keyPath)
    {
        string key = keyPath.Trim().Trim('\\');
        foreach (var (full, shortName) in new[]
                 {
                     ("HKEY_LOCAL_MACHINE", "HKLM"), ("HKEY_CURRENT_USER", "HKCU"),
                     ("HKEY_USERS", "HKU"), ("HKEY_CLASSES_ROOT", "HKCR"),
                 })
        {
            if (key.StartsWith(full, StringComparison.OrdinalIgnoreCase))
            {
                return shortName + key[full.Length..];
            }
        }

        return key;
    }

    private static (RegistryHive? Hive, string SubKey) Split(string keyPath)
    {
        string key = ToRegExePath(keyPath);
        int slash = key.IndexOf('\\');
        if (slash < 0)
        {
            return (null, string.Empty);
        }

        RegistryHive? hive = key[..slash].ToUpperInvariant() switch
        {
            "HKLM" => RegistryHive.LocalMachine,
            "HKCU" => RegistryHive.CurrentUser,
            "HKU" => RegistryHive.Users,
            _ => null,
        };
        return (hive, key[(slash + 1)..]);
    }

    private bool RunReg(string arguments, out string output)
    {
        string reg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(reg, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(RegTimeout))
            {
                process.Kill();
                output = "Batas waktu reg.exe";
                return false;
            }

            output = (stdout.Result + stderr.Result).Trim();
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "reg.exe gagal dijalankan");
            output = ex.Message;
            return false;
        }
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"RegistryBackupService({backupRoot})");
}

namespace MyUninstaller.Core.Safety;

/// <summary>
/// Daftar lindung registri (PRD). Path memakai nama hive lengkap, misalnya
/// HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Vendor\App.
/// </summary>
public static class ProtectedRegistryKeys
{
    // Pohon yang tidak pernah disentuh (beserta seluruh isinya), kecuali pengecualian di bawah.
    private static readonly string[] ForbiddenTrees =
    [
        @"HKEY_LOCAL_MACHINE\SYSTEM",
        @"HKEY_LOCAL_MACHINE\SAM",
        @"HKEY_LOCAL_MACHINE\SECURITY",
        @"HKEY_LOCAL_MACHINE\BCD00000000",
        @"HKEY_LOCAL_MACHINE\HARDWARE",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\RegisteredApplications",
        @"HKEY_CURRENT_USER\Software\Microsoft",
        @"HKEY_CURRENT_USER\Software\Policies",
        @"HKEY_CURRENT_USER\Software\Classes",
        @"HKEY_CURRENT_USER\Control Panel",
        @"HKEY_CURRENT_USER\Environment",
        @"HKEY_USERS\.DEFAULT",
        @"HKEY_CLASSES_ROOT",
    ];

    // Subkunci di dalam pohon Microsoft yang boleh dihapus SATU entrinya (bukan wadahnya).
    private static readonly string[] AllowedEntryContainers =
    [
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths",
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\App Paths",

        // Kelas COM milik program (mode Super; hanya bila servernya ada di folder program).
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\CLSID",
        @"HKEY_CURRENT_USER\Software\Classes\CLSID",
    ];

    // Kunci yang NILAI-nya boleh dihapus satu per satu (kuncinya sendiri tetap dilindungi).
    private static readonly string[] ValueContainers =
    [
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run",
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\RunOnce",
    ];

    /// <summary>
    /// True bila nilai registri tidak boleh dihapus. Nilai di kunci Run/RunOnce boleh; selain itu ikut aturan kunci.
    /// Nilai bawaan (nama kosong) tidak pernah dihapus satu per satu.
    /// </summary>
    public static bool IsProtectedValue(string? keyPath, string? valueName)
    {
        if (string.IsNullOrEmpty(valueName) || string.IsNullOrWhiteSpace(keyPath))
        {
            return true;
        }

        string key = Normalize(keyPath);
        return !ValueContainers.Any(c => key.Equals(c, StringComparison.OrdinalIgnoreCase)) && IsProtected(keyPath);
    }

    // Wadah: kunci itu sendiri tidak boleh dihapus, subkunci programnya boleh.
    private static readonly string[] Containers =
    [
        "HKEY_LOCAL_MACHINE",
        "HKEY_CURRENT_USER",
        "HKEY_USERS",
        @"HKEY_LOCAL_MACHINE\SOFTWARE",
        @"HKEY_CURRENT_USER\Software",
    ];

    /// <summary>True bila kunci tidak boleh dihapus.</summary>
    public static bool IsProtected(string? keyPath)
    {
        if (string.IsNullOrWhiteSpace(keyPath))
        {
            return true;
        }

        string key = Normalize(keyPath);
        if (key.Length == 0)
        {
            return true;
        }

        foreach (string container in Containers.Concat(AllowedEntryContainers))
        {
            if (key.Equals(container, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Tepat satu tingkat di bawah wadah entri yang diizinkan: boleh (misalnya Uninstall\7-Zip).
        foreach (string container in AllowedEntryContainers)
        {
            if (key.StartsWith(container + @"\", StringComparison.OrdinalIgnoreCase)
                && !key[(container.Length + 1)..].Contains('\\'))
            {
                return false;
            }
        }

        foreach (string tree in ForbiddenTrees)
        {
            if (key.Equals(tree, StringComparison.OrdinalIgnoreCase)
                || key.StartsWith(tree + @"\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Hanya hive yang dikenal boleh dihapus isinya.
        return !(key.StartsWith(@"HKEY_LOCAL_MACHINE\", StringComparison.OrdinalIgnoreCase)
                 || key.StartsWith(@"HKEY_CURRENT_USER\", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Menyeragamkan path: singkatan hive (HKLM/HKCU) jadi nama lengkap, tanpa garis miring di ujung,
    /// dan WOW6432Node diperlakukan sama dengan view 64-bit untuk pengecekan lindung.
    /// </summary>
    public static string Normalize(string keyPath)
    {
        string key = keyPath.Trim().Trim('\\').Replace('/', '\\');
        while (key.Contains(@"\\", StringComparison.Ordinal))
        {
            key = key.Replace(@"\\", @"\", StringComparison.Ordinal);
        }

        int slash = key.IndexOf('\\');
        string hive = slash < 0 ? key : key[..slash];
        string rest = slash < 0 ? string.Empty : key[slash..];
        hive = hive.ToUpperInvariant() switch
        {
            "HKLM" => "HKEY_LOCAL_MACHINE",
            "HKCU" => "HKEY_CURRENT_USER",
            "HKU" => "HKEY_USERS",
            "HKCR" => "HKEY_CLASSES_ROOT",
            var other => other,
        };

        key = hive + rest;
        foreach (string prefix in new[] { @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node", @"HKEY_CURRENT_USER\Software\WOW6432Node" })
        {
            if (key.StartsWith(prefix + @"\", StringComparison.OrdinalIgnoreCase) || key.Equals(prefix, StringComparison.OrdinalIgnoreCase))
            {
                key = key[..prefix.LastIndexOf('\\')] + key[prefix.Length..];
            }
        }

        return key;
    }
}

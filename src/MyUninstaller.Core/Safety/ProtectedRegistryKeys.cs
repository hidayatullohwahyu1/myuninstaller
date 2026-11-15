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
        return !ValueContainers.Any(c => key.Equals(c, StringComparison.OrdinalIgnoreCase))
               && !IsOpenWithProgidsKey(key)
               && IsProtected(keyPath);
    }

    // ---------- Asosiasi berkas (Classes) ----------

    private static readonly string[] ClassesRoots =
    [
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes",
        @"HKEY_CURRENT_USER\Software\Classes",
    ];

    // Kelas inti Windows dan wadah di Classes: tidak pernah dihapus walau tampak menunjuk ke folder program.
    private static readonly HashSet<string> CoreClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "*", "AllFilesystemObjects", "Applications", "AppID", "CLSID", "Directory", "Drive", "Folder", "Interface",
        "Local Settings", "MIME", "Network", "Printers", "SystemFileAssociations", "TypeLib", "Unknown", "WOW6432Node",
        "exefile", "lnkfile", "batfile", "cmdfile", "comfile", "piffile", "regfile", "scrfile", "txtfile", "htmlfile",
        "inifile", "dllfile", "sysfile", "mscfile", "Msi.Package", "Msi.Patch", "http", "https", "mailto", "ftp",
        "InternetShortcut", "CompressedFolder", "SearchFolder", "LibraryFolder", "DesktopBackground",
    };

    private static readonly string[] ProtectedClassPrefixes = ["Microsoft.", "Windows.", "AppX", "ms-", "MSEdge", "Shell."];

    /// <summary>
    /// Entri asosiasi berkas yang BOLEH dihapus: tepat satu tingkat di bawah Classes (ProgID) atau di bawah
    /// Classes\Applications (aplikasi "Buka dengan"). Ekstensi (.txt), kelas inti Windows, dan ProgID milik
    /// Microsoft/AppX tidak pernah boleh. Pemindai hanya mengusulkannya bila perintahnya menunjuk ke folder program.
    /// </summary>
    public static bool IsAllowedClassEntry(string keyPath)
    {
        string key = Normalize(keyPath);
        foreach (string root in ClassesRoots)
        {
            foreach (string container in new[] { root, root + @"\Applications" })
            {
                if (!key.StartsWith(container + @"\", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string name = key[(container.Length + 1)..];
                if (name.Contains('\\'))
                {
                    continue;
                }

                return !IsProtectedClassName(name);
            }
        }

        return false;
    }

    /// <summary>Nama kelas yang tidak pernah boleh dihapus: ekstensi, kelas inti Windows, ProgID Microsoft/AppX.</summary>
    public static bool IsProtectedClassName(string name) =>
        name.Length == 0
        || name.StartsWith('.')
        || CoreClasses.Contains(name)
        || ProtectedClassPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Classes\.ext\OpenWithProgids: nilai ProgID program yang dihapus boleh dibuang satu per satu.</summary>
    private static bool IsOpenWithProgidsKey(string normalizedKey) =>
        ClassesRoots.Any(root => normalizedKey.StartsWith(root + @"\.", StringComparison.OrdinalIgnoreCase)
                                 && normalizedKey.EndsWith(@"\OpenWithProgids", StringComparison.OrdinalIgnoreCase)
                                 && normalizedKey[(root.Length + 1)..^@"\OpenWithProgids".Length].IndexOf('\\') < 0);

    /// <summary>Satu-satunya kunci yang nilai PATH-nya boleh disunting: PATH pengguna (HKCU\Environment).</summary>
    public static bool IsEditablePathVariable(string? keyPath) =>
        keyPath is not null && Normalize(keyPath).Equals(@"HKEY_CURRENT_USER\Environment", StringComparison.OrdinalIgnoreCase);

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

        if (IsAllowedClassEntry(key))
        {
            return false;
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

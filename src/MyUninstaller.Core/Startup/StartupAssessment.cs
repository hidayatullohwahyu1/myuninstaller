using MyUninstaller.Core.Hunter;
using MyUninstaller.Core.Leftovers;
using MyUninstaller.Core.Models;

namespace MyUninstaller.Core.Startup;

/// <summary>Status program awal terhadap daftar program terpasang.</summary>
public enum StartupStatus
{
    /// <summary>Milik program terpasang.</summary>
    Owned,

    /// <summary>Berkasnya ada, tetapi tidak tercatat sebagai program terpasang (misalnya ikut terpasang saat browsing).</summary>
    UnknownOwner,

    /// <summary>Exe yang dijalankan sudah tidak ada: entri sisa yang hanya membuat galat saat Windows mulai.</summary>
    MissingFile,

    /// <summary>Bagian dari Windows (exe di folder Windows), misalnya ikon Keamanan Windows di baki sistem.</summary>
    Windows,
}

public static class StartupAssessment
{
    /// <summary>
    /// Pemilik program awal: (1) path exe di dalam folder program terpasang; (2) nama produk exe (info versi) sama
    /// dengan / termuat di nama program, dan penerbitnya cocok bila diketahui. Contoh: EEventManager.exe →
    /// "EPSON Event Manager" → program "Epson Event Manager" yang tidak mencatat InstallLocation.
    /// </summary>
    public static InstalledProgram? FindOwner(StartupEntry entry, IReadOnlyList<InstalledProgram> programs)
    {
        if (ProgramMatcher.FindByExecutable(entry.ExecutablePath, programs) is { } byPath)
        {
            return byPath;
        }

        string product = NameMatcher.Normalize(entry.ProductName);
        if (product.Length < 5 || NameMatcher.IsGeneric(product))
        {
            return null;
        }

        string company = NameMatcher.NormalizePublisher(entry.CompanyName);
        return programs
            .Where(p => !p.IsSystemComponent || programs.All(o => o.IsSystemComponent))
            .Select(p => (Program: p, Name: NameMatcher.Normalize(p.DisplayName), Publisher: NameMatcher.NormalizePublisher(p.Publisher)))
            .Where(p => p.Name.Length > 0 && (p.Name == product || p.Name.Contains(product, StringComparison.Ordinal)))
            .Where(p => company.Length == 0 || p.Publisher.Length == 0
                        || p.Publisher.Contains(company, StringComparison.Ordinal) || company.Contains(p.Publisher, StringComparison.Ordinal))
            .OrderBy(p => p.Name.Length) // paling spesifik (nama terpendek yang memuat produk) menang
            .Select(p => p.Program)
            .FirstOrDefault();
    }

    public static StartupStatus Classify(StartupEntry entry, InstalledProgram? owner, Func<string, bool> fileExists, string? windowsFolder = null)
    {
        if (entry.ExecutablePath is { } exe && Path.IsPathFullyQualified(exe) && !fileExists(exe))
        {
            return StartupStatus.MissingFile;
        }

        if (owner is null && entry.ExecutablePath is { } path && !string.IsNullOrEmpty(windowsFolder)
            && path.StartsWith(Path.TrimEndingDirectorySeparator(windowsFolder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return StartupStatus.Windows;
        }

        return owner is null ? StartupStatus.UnknownOwner : StartupStatus.Owned;
    }

    /// <summary>
    /// Exe di AppData (bukan AppData\Local\Programs): lokasi khas program per-pengguna yang terpasang tanpa installer
    /// biasa. Hanya keterangan tambahan; bukan bukti berbahaya.
    /// </summary>
    public static bool RunsFromAppData(string? exe, string roamingAppData, string localAppData)
    {
        if (string.IsNullOrWhiteSpace(exe))
        {
            return false;
        }

        string programs = Path.Combine(localAppData, "Programs") + Path.DirectorySeparatorChar;
        return (exe.StartsWith(roamingAppData + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || exe.StartsWith(localAppData + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
               && !exe.StartsWith(programs, StringComparison.OrdinalIgnoreCase);
    }
}

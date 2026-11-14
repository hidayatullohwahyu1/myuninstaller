using MyUninstaller.Core.Models;
using MyUninstaller.Core.Scanning;

namespace MyUninstaller.Core.Hunter;

/// <summary>
/// Mode Pemburu: mencari program terpasang pemilik sebuah berkas exe (dari jendela, pintasan, atau ikon baki).
/// </summary>
public static class ProgramMatcher
{
    /// <summary>
    /// Mencocokkan exe dengan program. Urutan: InstallLocation yang memuat exe (paling spesifik/panjang menang),
    /// lalu folder DisplayIcon, lalu folder exe penghapus instalan. Program yang tampil diutamakan atas komponen sistem.
    /// </summary>
    public static InstalledProgram? FindByExecutable(string? executablePath, IEnumerable<InstalledProgram> programs)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath))
        {
            return null;
        }

        string exe = Path.GetFullPath(executablePath);
        InstalledProgram? best = null;
        int bestScore = 0;

        foreach (var program in programs)
        {
            int score = Score(exe, program);
            if (score == 0)
            {
                continue;
            }

            // Komponen sistem hanya menang bila tidak ada program biasa yang cocok setara.
            if (!program.IsSystemComponent)
            {
                score += 1;
            }

            if (score > bestScore)
            {
                best = program;
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>Mencocokkan nama (misalnya teks tooltip ikon baki) dengan DisplayName. Untuk cadangan saja.</summary>
    public static InstalledProgram? FindByName(string? name, IEnumerable<InstalledProgram> programs)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string text = name.Trim();
        var visible = programs.Where(p => !p.IsSystemComponent).ToList();

        return visible.FirstOrDefault(p => p.DisplayName.Equals(text, StringComparison.CurrentCultureIgnoreCase))
            ?? visible
                .Where(p => text.Contains(p.DisplayName, StringComparison.CurrentCultureIgnoreCase)
                            || p.DisplayName.Contains(text, StringComparison.CurrentCultureIgnoreCase))
                .OrderByDescending(p => p.DisplayName.Length)
                .FirstOrDefault();
    }

    // Skor = panjang folder × bobot, supaya kecocokan InstallLocation mengalahkan kecocokan folder ikon,
    // dan folder ikon mengalahkan folder penghapus instalan dengan panjang yang sama.
    private static int Score(string exe, InstalledProgram program)
    {
        if (Contains(program.InstallLocation, exe) is { } installLength)
        {
            return installLength * 4;
        }

        if (IconLocation.Parse(program.IconPath)?.Path is { } iconPath && Contains(Path.GetDirectoryName(iconPath), exe) is { } iconLength)
        {
            return iconLength * 2;
        }

        if (IconLocation.ExecutableFromCommandLine(program.UninstallString) is { } uninstaller
            && Contains(Path.GetDirectoryName(uninstaller), exe) is { } uninstallLength)
        {
            return uninstallLength;
        }

        return 0;
    }

    /// <summary>Panjang folder bila exe berada di dalamnya, selain itu null. Folder akar/pendek ditolak.</summary>
    private static int? Contains(string? folder, string exe)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
        {
            return null;
        }

        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Trim('"'))) + Path.DirectorySeparatorChar;
        if (normalized.Length <= 4 || IsSharedRoot(normalized))
        {
            return null;
        }

        return exe.StartsWith(normalized, StringComparison.OrdinalIgnoreCase) ? normalized.Length : null;
    }

    private static bool IsSharedRoot(string folder)
    {
        static string F(Environment.SpecialFolder f) =>
            Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(f)) + Path.DirectorySeparatorChar;

        return folder.Equals(F(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase)
            || folder.Equals(F(Environment.SpecialFolder.ProgramFilesX86), StringComparison.OrdinalIgnoreCase)
            || folder.Equals(F(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)
            || folder.Equals(F(Environment.SpecialFolder.System), StringComparison.OrdinalIgnoreCase)
            || folder.Equals(F(Environment.SpecialFolder.SystemX86), StringComparison.OrdinalIgnoreCase);
    }
}

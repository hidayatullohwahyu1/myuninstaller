using MyUninstaller.Core.Models;
using MyUninstaller.Core.Scanning;

namespace MyUninstaller.Core.Leftovers;

/// <summary>Mencari sisa instalan satu program (hanya baca).</summary>
public interface ILeftoverScanner
{
    /// <param name="program">Program yang baru dihapus (data sebelum dihapus).</param>
    /// <param name="stillInstalled">Program lain yang masih terpasang; miliknya tidak pernah dilaporkan.</param>
    /// <param name="deep">Mode Super: layanan, tugas terjadwal, CLSID, dan pencarian nama di seluruh drive.</param>
    Task<IReadOnlyList<LeftoverItem>> ScanAsync(
        InstalledProgram program,
        IReadOnlyList<InstalledProgram> stillInstalled,
        bool deep,
        CancellationToken cancellationToken = default);
}

public static class InstallLocationInference
{
    /// <summary>
    /// Banyak entri tidak mengisi InstallLocation. Tebak dari folder exe penghapus instalan, lalu folder
    /// DisplayIcon, asalkan bukan folder bersama (Program Files, Windows, profil, dsb.).
    /// </summary>
    public static InstalledProgram WithInferredLocation(InstalledProgram program, InstallLocationRules rules)
    {
        if (!string.IsNullOrWhiteSpace(program.InstallLocation))
        {
            return program;
        }

        string? fromUninstaller = Folder(IconLocation.ExecutableFromCommandLine(program.UninstallString));
        string? fromIcon = Folder(IconLocation.Parse(program.IconPath)?.Path);

        foreach (string? found in new[] { fromUninstaller, fromIcon })
        {
            // Uninstaller sering ada di subfolder umum ("...\Adobe Creative Cloud\Utils", "...\Epson Scan 2\Setup"):
            // naik ke induknya selama induk itu masih folder program, bukan folder bersama.
            string? candidate = found;
            while (candidate is not null && GenericSubfolders.Contains(Path.GetFileName(candidate))
                   && Path.GetDirectoryName(candidate) is { } parent && rules.IsMeasurable(parent))
            {
                candidate = parent;
            }

            if (candidate is not null && rules.IsMeasurable(candidate) && !IsInsideWindows(candidate) && !IsInstallerCache(candidate))
            {
                return program with { InstallLocation = candidate };
            }
        }

        return program;

        static string? Folder(string? file) =>
            file is not null && Path.IsPathFullyQualified(file) ? Path.GetDirectoryName(file) : null;
    }

    private static readonly HashSet<string> GenericSubfolders = new(StringComparer.OrdinalIgnoreCase)
    {
        // Sengaja sempit: naik ke induk berisiko (folder induk bisa milik penerbit dengan banyak program).
        "bin", "bin64", "x64", "x86", "utils", "util", "setup", "uninstall", "uninst", "uninstaller", "installer",
    };

    private static bool IsInsideWindows(string folder) =>
        folder.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    // Uninstaller yang disalin ke cache (ProgramData\Package Cache, {GUID} di Installer) bukan folder program.
    private static bool IsInstallerCache(string folder) =>
        folder.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase)
        || folder.Contains(@"\Installer\", StringComparison.OrdinalIgnoreCase)
        || folder.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase);
}

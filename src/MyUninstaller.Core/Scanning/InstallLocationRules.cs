namespace MyUninstaller.Core.Scanning;

/// <summary>
/// Menentukan apakah InstallLocation layak dihitung ukurannya. Folder bersama (akar drive, Windows,
/// Program Files, ProgramData, profil pengguna) ditolak karena isinya bukan milik satu program.
/// </summary>
public sealed class InstallLocationRules
{
    private readonly HashSet<string> _sharedRoots;

    public InstallLocationRules(IEnumerable<string> sharedRoots)
    {
        _sharedRoots = new HashSet<string>(
            sharedRoots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(Normalize),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Folder bersama milik sistem saat ini.</summary>
    public static InstallLocationRules ForCurrentSystem()
    {
        static string F(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);

        string userProfile = F(Environment.SpecialFolder.UserProfile);
        return new InstallLocationRules(
        [
            F(Environment.SpecialFolder.Windows),
            F(Environment.SpecialFolder.System),
            F(Environment.SpecialFolder.SystemX86),
            F(Environment.SpecialFolder.ProgramFiles),
            F(Environment.SpecialFolder.ProgramFilesX86),
            F(Environment.SpecialFolder.CommonProgramFiles),
            F(Environment.SpecialFolder.CommonProgramFilesX86),
            F(Environment.SpecialFolder.CommonApplicationData),
            F(Environment.SpecialFolder.ApplicationData),
            F(Environment.SpecialFolder.LocalApplicationData),
            Path.Combine(F(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            F(Environment.SpecialFolder.Desktop),
            F(Environment.SpecialFolder.MyDocuments),
            userProfile,
            Path.GetDirectoryName(userProfile) ?? string.Empty, // C:\Users
        ]);
    }

    public bool IsMeasurable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        string normalized = Normalize(path);

        // Akar drive (C:\) atau share jaringan tidak dihitung.
        if (Path.GetPathRoot(normalized) is { } root && Normalize(root).Equals(normalized, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        return !_sharedRoots.Contains(normalized);
    }

    private static string Normalize(string path)
    {
        string full = Path.GetFullPath(path.Trim().Trim('"'));
        return full.Length > 3 ? full.TrimEnd('\\', '/') : full;
    }
}

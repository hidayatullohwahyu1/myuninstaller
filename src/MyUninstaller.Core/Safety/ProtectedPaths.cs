namespace MyUninstaller.Core.Safety;

/// <summary>
/// Daftar lindung (PRD): path dan kunci registri yang tidak pernah dihapus. Dicek saat memindai DAN
/// tepat sebelum setiap penghapusan.
/// </summary>
/// <remarks>
/// Dua jenis aturan:
/// <list type="bullet">
/// <item><b>Pohon terlarang</b>: path itu sendiri dan semua isinya (misalnya C:\Windows, Common Files, folder Microsoft).</item>
/// <item><b>Wadah</b>: path itu sendiri dilarang, tetapi subfolder milik program boleh (misalnya C:\Program Files,
/// %AppData%, C:\Users, akar drive).</item>
/// </list>
/// </remarks>
public sealed class ProtectedPaths
{
    private readonly List<string> _forbiddenTrees;
    private readonly HashSet<string> _containers;

    public ProtectedPaths(IEnumerable<string> forbiddenTrees, IEnumerable<string> containers)
    {
        _forbiddenTrees = [.. forbiddenTrees.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase)];
        _containers = new HashSet<string>(
            containers.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Normalize),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Daftar lindung untuk sistem saat ini.</summary>
    /// <param name="appDataRoot">Folder data aplikasi ini (%ProgramData%\MyUninstaller) — juga dilindungi.</param>
    public static ProtectedPaths ForCurrentSystem(string appDataRoot)
    {
        static string F(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);

        string programFiles = F(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = F(Environment.SpecialFolder.ProgramFilesX86);
        string programData = F(Environment.SpecialFolder.CommonApplicationData);
        string roaming = F(Environment.SpecialFolder.ApplicationData);
        string local = F(Environment.SpecialFolder.LocalApplicationData);
        string userProfile = F(Environment.SpecialFolder.UserProfile);
        string users = Path.GetDirectoryName(userProfile) ?? string.Empty;
        string localLow = Path.Combine(userProfile, "AppData", "LocalLow");
        string systemDrive = Path.GetPathRoot(F(Environment.SpecialFolder.Windows)) ?? @"C:\";

        string[] forbidden =
        [
            F(Environment.SpecialFolder.Windows),
            F(Environment.SpecialFolder.CommonProgramFiles),
            F(Environment.SpecialFolder.CommonProgramFilesX86),
            Path.Combine(programFiles, "Microsoft"),
            Path.Combine(programFiles, "WindowsApps"),
            Path.Combine(programFiles, "Windows Defender"),
            Path.Combine(programFiles, "Windows NT"),
            Path.Combine(programFiles, "dotnet"),
            Path.Combine(programFilesX86, "Microsoft"),
            Path.Combine(programFilesX86, "Windows Defender"),
            Path.Combine(programFilesX86, "Windows NT"),
            Path.Combine(programFilesX86, "dotnet"),
            Path.Combine(programData, "Microsoft"),
            Path.Combine(programData, "Packages"),
            Path.Combine(roaming, "Microsoft"),
            Path.Combine(local, "Microsoft"),
            Path.Combine(local, "Packages"),
            Path.Combine(localLow, "Microsoft"),
            Path.Combine(users, "Default"),
            Path.Combine(systemDrive, "System Volume Information"),
            Path.Combine(systemDrive, "Recovery"),
            Path.Combine(systemDrive, "$Recycle.Bin"),
            Path.Combine(systemDrive, "Boot"),
            appDataRoot,
        ];

        string[] containers =
        [
            programFiles,
            programFilesX86,
            programData,
            roaming,
            local,
            localLow,
            Path.Combine(local, "Programs"),
            Path.Combine(local, "Temp"),
            userProfile,
            users,
            Path.Combine(users, "Public"),
            F(Environment.SpecialFolder.DesktopDirectory),
            F(Environment.SpecialFolder.CommonDesktopDirectory),
            F(Environment.SpecialFolder.MyDocuments),
            F(Environment.SpecialFolder.CommonDocuments),
            F(Environment.SpecialFolder.StartMenu),
            F(Environment.SpecialFolder.Programs),
            F(Environment.SpecialFolder.CommonStartMenu),
            F(Environment.SpecialFolder.CommonPrograms),
            F(Environment.SpecialFolder.Startup),
            F(Environment.SpecialFolder.CommonStartup),
            Path.Combine(userProfile, "Downloads"),
            Path.Combine(userProfile, "AppData"),
        ];

        return new ProtectedPaths(forbidden, containers);
    }

    /// <summary>True bila path tidak boleh dihapus/dipindah ke karantina.</summary>
    public bool IsProtected(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return true; // Path tidak jelas tidak pernah dihapus.
        }

        string normalized;
        try
        {
            normalized = Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }

        // Akar drive (C:\) dan share jaringan.
        string? root = Path.GetPathRoot(normalized);
        if (root is null || Normalize(root).Equals(normalized, StringComparison.OrdinalIgnoreCase) || normalized.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }

        if (_containers.Contains(normalized))
        {
            return true;
        }

        foreach (string tree in _forbiddenTrees)
        {
            if (normalized.Equals(tree, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(tree + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static string Normalize(string path)
    {
        string full = Path.GetFullPath(path.Trim().Trim('"'));
        return full.Length > 3 ? full.TrimEnd('\\', '/') : full;
    }
}

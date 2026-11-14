using MyUninstaller.Core.Leftovers;
using MyUninstaller.Core.Models;

namespace MyUninstaller.Core.Portable;

/// <summary>
/// Aturan deteksi program portable (versi 2.0). Tujuan utamanya: JANGAN salah mengira folder milik program terpasang,
/// proyek pengembangan, atau komponen Microsoft sebagai program portable.
/// </summary>
public static class PortableRules
{
    /// <summary>Kedalaman maksimum dari folder akar (Unduhan\arsip\program\app.exe = 3).</summary>
    public const int MaxDepth = 3;

    /// <summary>Batas jumlah folder yang dikunjungi per pemindaian (drive besar tidak membuat pemindaian berjam-jam).</summary>
    public const int MaxFoldersVisited = 50_000;

    // Folder hasil build/dependensi/alat pengembang: isinya exe tetapi bukan program yang "dipasang".
    private static readonly HashSet<string> SkippedFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "obj", "node_modules", "packages", "target", "build", "dist", "out", "debug", "release",
        "venv", ".venv", "env", "__pycache__", "site-packages", "scripts", "vendor", "third_party",
        // Folder sistem di akar drive.
        "windows", "program files", "program files (x86)", "programdata", "users", "perflogs", "recovery",
        "system volume information", "$recycle.bin", "$windows.~bt", "$windows.~ws", "$winreagent", "boot",
        "config.msi", "msocache", "onedrivetemp", "intel", "amd", "nvidia", "drivers", "inetpub", "windows.old",
        "xboxgames", "steamlibrary",
    };

    // Penanda akar proyek: seluruh pohon di bawahnya dilewati.
    private static readonly string[] ProjectMarkers =
    [
        ".git", ".hg", ".svn", "package.json", "cargo.toml", "pyproject.toml", "go.mod", "cmakelists.txt", "pom.xml",
        "build.gradle", "makefile", "composer.json", "gemfile",
    ];

    private static readonly string[] ProjectMarkerExtensions = [".sln", ".slnx", ".csproj", ".vcxproj", ".fsproj", ".vbproj"];

    // Exe pembantu: tidak dipilih sebagai exe utama dan tidak membuat folder dianggap program.
    private static readonly string[] HelperNameParts =
    [
        "setup", "install", "unins", "uninst", "update", "updater", "crash", "report", "helper", "elevate",
        "vc_redist", "vcredist", "dxsetup", "dotnet", "redist", "launcherhelper", "notification_helper",
    ];

    public static bool IsSkippedFolderName(string name) =>
        name.StartsWith('.') || name.StartsWith('$') || SkippedFolderNames.Contains(name);

    // Folder penyimpanan pengguna: isinya ditelusuri, tetapi folder itu sendiri TIDAK PERNAH dijadikan program
    // (exe di dalamnya biasanya installer atau satu exe lepas, bukan pemilik folder).
    private static readonly string[] StorageFolderNameParts =
    [
        "download", "unduh", "desktop", "document", "dokumen", "compressed", "temp", "tmp", "installer", "setup",
        "software", "aplikasi", "program", "master", "backup", "cadangan", "titip", "share", "data", "file",
    ];

    // Berkas data pengguna: arsip, installer, dokumen, media. Banyak berkas seperti ini = folder penyimpanan.
    private static readonly HashSet<string> UserDataExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".iso", ".msi", ".msix", ".appx", ".apk", ".torrent",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt",
        ".mp4", ".mkv", ".avi", ".mov", ".mp3", ".wav", ".flac", ".jpg", ".jpeg", ".png", ".heic", ".psd",
    };

    /// <summary>Batas berkas data pengguna di folder program (readme.pdf, ikon .png masih wajar).</summary>
    public const int MaxUserDataFiles = 3;

    /// <summary>
    /// True bila folder tampak sebagai folder penyimpanan, bukan folder program: namanya umum (Download, Desktop,
    /// Compressed, ...) atau berisi banyak arsip/installer/dokumen/media.
    /// </summary>
    public static bool IsStorageFolder(string folderName, IEnumerable<string> fileNames)
    {
        if (StorageFolderNameParts.Any(part => folderName.Contains(part, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return fileNames.Count(f => UserDataExtensions.Contains(Path.GetExtension(f))) > MaxUserDataFiles;
    }

    /// <summary>
    /// True bila kandidat tampak sebagai program terpasang yang folder-nya tidak tercatat (contoh: Python dari
    /// installer Burn hanya mencatat Package Cache). Cocok bila nama produk/folder ada di nama program terpasang,
    /// atau penerbitnya sama dan namanya beririsan. Lebih baik melewatkan program portable daripada salah hapus.
    /// </summary>
    public static bool MatchesInstalledProgram(string folderName, ExecutableInfo main, IEnumerable<InstalledProgram> installed)
    {
        var names = new[] { main.ProductName, main.Description, folderName, Path.GetFileNameWithoutExtension(main.Path) }
            .Select(NameMatcher.Normalize)
            .Where(n => n.Length >= 4 && !NameMatcher.IsGeneric(n))
            .Distinct()
            .ToList();
        string publisher = NameMatcher.NormalizePublisher(main.Company);

        foreach (var program in installed)
        {
            string installedName = NameMatcher.Normalize(program.DisplayName);
            if (installedName.Length == 0)
            {
                continue;
            }

            if (names.Any(n => installedName.Contains(n, StringComparison.Ordinal)))
            {
                return true;
            }

            bool samePublisher = publisher.Length >= 3 && NameMatcher.NormalizePublisher(program.Publisher) == publisher;
            if (samePublisher && names.Any(n => n.Contains(installedName, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True bila folder adalah akar proyek pengembangan (berisi .git, .sln, package.json, ...).</summary>
    public static bool IsProjectFolder(IEnumerable<string> entryNames) =>
        entryNames.Any(n => ProjectMarkers.Contains(n, StringComparer.OrdinalIgnoreCase)
                            || ProjectMarkerExtensions.Any(e => n.EndsWith(e, StringComparison.OrdinalIgnoreCase)));

    public static bool IsHelperExecutable(string fileName)
    {
        string name = Path.GetFileNameWithoutExtension(fileName);
        return HelperNameParts.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Komponen Microsoft tidak pernah ditawarkan (aturan keamanan: milik Microsoft tidak disentuh).</summary>
    public static bool IsMicrosoft(ExecutableInfo info) =>
        info.Company?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Exe utama: yang namanya cocok dengan nama folder, lalu yang terbesar. Null bila hanya ada exe pembantu.
    /// </summary>
    public static ExecutableInfo? ChooseMainExecutable(string folderName, IReadOnlyList<ExecutableInfo> executables)
    {
        var candidates = executables.Where(e => !IsHelperExecutable(e.Path)).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        string folder = NameMatcher.Normalize(folderName);
        return candidates
            .OrderByDescending(e => NameMatches(folder, NameMatcher.Normalize(Path.GetFileNameWithoutExtension(e.Path))))
            .ThenByDescending(e => e.Length)
            .First();
    }

    private static bool NameMatches(string folder, string exe) =>
        folder.Length > 0 && exe.Length > 0 && (folder.Contains(exe, StringComparison.Ordinal) || exe.Contains(folder, StringComparison.Ordinal));

    /// <summary>Nama tampilan: ProductName → FileDescription → nama folder.</summary>
    public static string DisplayName(string folderName, ExecutableInfo main)
    {
        foreach (string? candidate in new[] { main.ProductName, main.Description })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && candidate.Trim().Length > 1)
            {
                return candidate.Trim();
            }
        }

        return folderName;
    }

    /// <summary>
    /// Folder yang dipakai program terpasang: InstallLocation, folder ikon, dan folder penghapus instalan.
    /// Folder kandidat yang sama dengan, di dalam, atau memuat salah satunya BUKAN program portable.
    /// </summary>
    public static IReadOnlyList<string> OwnedFolders(IEnumerable<InstalledProgram> installed)
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var program in installed)
        {
            Add(program.InstallLocation, isFile: false);
            Add(program.IconPath?.Split(',')[0], isFile: true);
            Add(ExecutableOf(program.UninstallString), isFile: true);
        }

        return [.. folders];

        void Add(string? path, bool isFile)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                string clean = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                if (!Path.IsPathFullyQualified(clean))
                {
                    return;
                }

                string? folder = isFile ? Path.GetDirectoryName(clean) : clean;
                if (folder is not null && Path.GetPathRoot(folder) != folder)
                {
                    folders.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)));
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Nilai registri rusak: abaikan.
            }
        }
    }

    /// <summary>True bila folder sama dengan atau berada DI DALAM folder program terpasang (pohonnya tidak dipindai).</summary>
    public static bool IsInsideOwned(string folder, IReadOnlyList<string> ownedFolders)
    {
        string candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return ownedFolders.Any(owned => candidate.Equals(owned, StringComparison.OrdinalIgnoreCase)
                                         || candidate.StartsWith(owned + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True bila folder terkait program terpasang: sama, di dalam, atau MEMUAT folder program itu.</summary>
    public static bool IsOwned(string folder, IReadOnlyList<string> ownedFolders)
    {
        string candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        foreach (string owned in ownedFolders)
        {
            if (candidate.Equals(owned, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(owned + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || owned.StartsWith(candidate + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Path exe dari UninstallString ("C:\a b\unins000.exe" /x atau C:\a\u.exe /S); null untuk MsiExec.</summary>
    public static string? ExecutableOf(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        string text = commandLine.Trim();
        if (text.StartsWith('"'))
        {
            int end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }

        int exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        string path = exe > 0 ? text[..(exe + 4)] : text;
        return path.Contains("msiexec", StringComparison.OrdinalIgnoreCase) ? null : path;
    }
}

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MyUninstaller.Core.Abstractions;
using MyUninstaller.Core.Models;
using MyUninstaller.Core.Portable;
using MyUninstaller.Core.Safety;
using MyUninstaller.Core.Usage;
using MyUninstaller.Infrastructure.FileSystem;

namespace MyUninstaller.Infrastructure.Portable;

/// <summary>
/// Mencari folder program tanpa installer (versi 2.0) di folder pengguna dan akar drive tetap.
/// Hanya membaca: nama folder, daftar exe, info versi exe, ukuran, pintasan. Tidak mengikuti junction/symlink.
/// </summary>
public sealed class PortableProgramScanner(
    IFolderSizeCalculator sizes,
    IUsageHistory usage,
    ProtectedPaths protectedPaths,
    ILogger<PortableProgramScanner> logger,
    IReadOnlyList<string>? shortcutFolders = null,
    IReadOnlyList<string>? defaultRoots = null,
    IReadOnlyList<string>? appDataRoots = null) : IPortableProgramScanner
{
    private static readonly EnumerationOptions FolderOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
    };

    private static readonly EnumerationOptions FileOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public IReadOnlyList<string> DefaultRoots { get; } = defaultRoots ?? CurrentSystemRoots();

    /// <summary>AppData Roaming dan Local: temuan di sini ditandai "periksa dulu".</summary>
    private readonly IReadOnlyList<string> _appDataRoots = appDataRoots ??
    [
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    ];

    // Anak AppData yang bukan program per-pengguna (atau sudah dipindai sebagai akar sendiri).
    private static readonly HashSet<string> AppDataSkipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "Programs", "Microsoft", "Packages", "Temp", "CrashDumps", "D3DSCache", "NVIDIA", "Comms", "ConnectedDevicesPlatform",
        "pip", "npm", "npm-cache", "Yarn", "NuGet", "Package Cache", "PeerDistRepub", "Publishers", "IsolatedStorage",
    };

    private static IReadOnlyList<string> CurrentSystemRoots()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.Combine(profile, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),

            // Program yang ikut terpasang saat browsing biasanya di sini (tanpa entri Uninstall).
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };

        try
        {
            roots.AddRange(DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Tanpa daftar drive: folder pengguna saja.
        }

        return [.. roots.Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static IReadOnlyList<string> CurrentShortcutFolders() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
    ];

    public IReadOnlyList<PortableProgram> Scan(IReadOnlyList<string> roots, IReadOnlyList<InstalledProgram> installed, CancellationToken cancellationToken)
    {
        var owned = PortableRules.OwnedFolders(installed);
        var found = new Dictionary<string, (string Folder, ExecutableInfo Main, bool InAppData)>(StringComparer.OrdinalIgnoreCase);
        int visited = 0;
        var clock = Stopwatch.StartNew();
        string? appDataRoot = null;

        foreach (string root in roots.Where(Directory.Exists))
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            appDataRoot = _appDataRoots.FirstOrDefault(a => Path.TrimEndingDirectorySeparator(a).Equals(full, StringComparison.OrdinalIgnoreCase)) is null ? null : full;
            Visit(root, 0);
        }

        logger.LogInformation("Program portable: {Count} kandidat dari {Visited} folder ({Ms} ms)", found.Count, visited, clock.ElapsedMilliseconds);
        if (found.Count == 0)
        {
            return [];
        }

        var shortcuts = ReadShortcuts(cancellationToken);
        UsageIndex history;
        try
        {
            history = usage.Load();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Riwayat pemakaian tidak bisa dibaca");
            history = UsageIndex.Empty;
        }

        var result = new List<PortableProgram>();
        foreach (var (folder, main, inAppData) in found.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string prefix = folder + Path.DirectorySeparatorChar;
            result.Add(new PortableProgram
            {
                Name = PortableRules.DisplayName(Path.GetFileName(folder), main),
                Folder = folder,
                MainExecutable = main.Path,
                IsInAppData = inAppData,
                Publisher = string.IsNullOrWhiteSpace(main.Company) ? null : main.Company.Trim(),
                Version = string.IsNullOrWhiteSpace(main.Version) ? null : main.Version.Trim(),
                SizeBytes = sizes.Measure(folder, cancellationToken),
                LastUsed = history.LastUsed(usage.ExecutablesIn(folder)),
                Shortcuts = [.. shortcuts.Where(s => s.Target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(s => s.Shortcut)],
            });
        }

        logger.LogInformation("Program portable: rincian selesai dalam {Ms} ms", clock.ElapsedMilliseconds);
        return [.. result.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];

        void Visit(string folder, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++visited > PortableRules.MaxFoldersVisited || PortableRules.IsInsideOwned(folder, owned))
            {
                return;
            }

            try
            {
                var names = Directory.EnumerateFileSystemEntries(folder, "*", FileOptions).Select(Path.GetFileName).OfType<string>().ToList();
                if (PortableRules.IsProjectFolder(names))
                {
                    return;
                }

                if (depth > 0 && TryCandidate(folder, names) is { } candidate)
                {
                    found.TryAdd(candidate.Folder, candidate);
                    return;
                }

                // AppData sangat besar (cache browser, dsb.); program per-pengguna ada di <AppData>\nama\versi.
                if (depth >= (appDataRoot is null ? PortableRules.MaxDepth : PortableRules.MaxAppDataDepth))
                {
                    return;
                }

                foreach (string sub in Directory.EnumerateDirectories(folder, "*", FolderOptions))
                {
                    string subName = Path.GetFileName(sub);
                    if (!PortableRules.IsSkippedFolderName(subName) && !(depth == 0 && appDataRoot is not null && AppDataSkipped.Contains(subName)))
                    {
                        Visit(sub, depth + 1);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Folder {Folder} dilewati", folder);
            }
        }

        (string Folder, ExecutableInfo Main, bool InAppData)? TryCandidate(string folder, IReadOnlyList<string> names)
        {
            if (!names.Any(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            var exes = Directory.EnumerateFiles(folder, "*.exe", FileOptions).Take(200).Select(ReadInfo).OfType<ExecutableInfo>().ToList();
            if (exes.Count == 0)
            {
                return null;
            }

            // ffmpeg\bin\ffmpeg.exe → program = ffmpeg (folder induk), bukan "bin".
            string programFolder = Path.GetFileName(folder).Equals("bin", StringComparison.OrdinalIgnoreCase)
                                   && Path.GetDirectoryName(folder) is { } parent && Path.GetPathRoot(parent) != parent
                ? parent
                : folder;
            programFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(programFolder));

            string programName = Path.GetFileName(programFolder);
            if (PortableRules.IsStorageFolder(programName, names))
            {
                return null;
            }

            // Di AppData: nama folder tingkat teratas (Roaming\<nama>\...) ikut dicocokkan dengan program terpasang.
            string? topName = appDataRoot is null ? null
                : Path.GetRelativePath(appDataRoot, programFolder).Split(Path.DirectorySeparatorChar)[0];

            var main = PortableRules.ChooseMainExecutable(programName, exes);
            if (main is null || PortableRules.IsMicrosoft(main) || PortableRules.IsOwned(programFolder, owned)
                || PortableRules.MatchesInstalledProgram(programName, main, installed, topName)
                || protectedPaths.IsProtected(programFolder))
            {
                return null;
            }

            return (programFolder, main, appDataRoot is not null);
        }
    }

    private ExecutableInfo? ReadInfo(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return new ExecutableInfo(path, new FileInfo(path).Length, info.ProductName, info.FileDescription, info.CompanyName,
                info.ProductVersion ?? info.FileVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogDebug(ex, "Info versi {Path} tidak terbaca", path);
            return null;
        }
    }

    private List<(string Shortcut, string Target)> ReadShortcuts(CancellationToken cancellationToken)
    {
        var list = new List<(string, string)>();
        foreach (string folder in (shortcutFolders ?? CurrentShortcutFolders()).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (string lnk in Directory.EnumerateFiles(folder, "*.lnk", new EnumerationOptions
                         {
                             IgnoreInaccessible = true,
                             RecurseSubdirectories = true,
                             AttributesToSkip = FileAttributes.ReparsePoint,
                         }))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (ShortcutResolver.GetTarget(lnk) is { } target && Path.IsPathFullyQualified(target))
                    {
                        list.Add((lnk, Path.GetFullPath(target)));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Pintasan di {Folder} tidak terbaca", folder);
            }
        }

        return list;
    }
}

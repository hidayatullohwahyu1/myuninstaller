using System.Diagnostics;
using System.Security;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using MyUninstaller.Core.Abstractions;
using MyUninstaller.Core.Leftovers;
using MyUninstaller.Core.Models;
using MyUninstaller.Core.Safety;
using MyUninstaller.Core.Scanning;
using MyUninstaller.Infrastructure.FileSystem;
using MyUninstaller.Infrastructure.Safety;
using MyUninstaller.Infrastructure.Scanning;

namespace MyUninstaller.Infrastructure.Leftovers;

/// <summary>
/// Pemindai sisa instalan (hanya baca). Normal: folder, pintasan, kunci SOFTWARE, kunci Uninstall,
/// App Paths, Run. Super: + layanan, tugas terjadwal, CLSID, dan pencarian nama di seluruh drive.
/// Daftar lindung dan milik program lain selalu dikecualikan.
/// </summary>
public sealed class LeftoverScanner(
    LeftoverScanRoots roots,
    ProtectedPaths protectedPaths,
    InstallLocationRules locationRules,
    IFolderSizeCalculator sizes,
    ILogger<LeftoverScanner> logger) : ILeftoverScanner
{
    private static readonly EnumerationOptions TopLevel = new() { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

    public LeftoverScanner(ProtectedPaths protectedPaths, InstallLocationRules locationRules, IFolderSizeCalculator sizes, ILogger<LeftoverScanner> logger)
        : this(LeftoverScanRoots.ForCurrentSystem(), protectedPaths, locationRules, sizes, logger)
    {
    }

    public Task<IReadOnlyList<LeftoverItem>> ScanAsync(
        InstalledProgram program,
        IReadOnlyList<InstalledProgram> stillInstalled,
        bool deep,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(program, stillInstalled, deep, cancellationToken), cancellationToken);

    private IReadOnlyList<LeftoverItem> Scan(InstalledProgram program, IReadOnlyList<InstalledProgram> stillInstalled, bool deep, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var effective = InstallLocationInference.WithInferredLocation(program, locationRules);
        var ctx = new LeftoverContext(effective, stillInstalled);
        var found = new Dictionary<string, LeftoverItem>();

        void Add(LeftoverKind kind, string location, LeftoverEvidence evidence, string? detail = null, string? valueName = null)
        {
            var item = new LeftoverItem
            {
                Kind = kind,
                Location = location,
                ValueName = valueName,
                Evidence = evidence,
                Score = ConfidenceScorer.Score(evidence, kind),
                ProgramName = program.DisplayName,
                Detail = detail,
            };

            // Bila ditemukan dua kali, simpan skor tertinggi.
            if (!found.TryGetValue(item.Key, out var existing) || existing.Score < item.Score)
            {
                found[item.Key] = item;
            }
        }

        // Setiap sumber dibungkus sendiri: satu sumber gagal tidak menghentikan yang lain.
        Safe("folder instalasi", () => ScanInstallLocation(ctx, Add));
        Safe("folder", () => ScanFolders(ctx, Add, ct));
        Safe("pintasan", () => ScanShortcuts(ctx, Add, ct));
        Safe("registri SOFTWARE", () => ScanSoftwareKeys(ctx, Add, ct));
        Safe("kunci Uninstall", () => ScanUninstallKey(program, Add));
        Safe("App Paths", () => ScanAppPaths(ctx, Add, ct));
        Safe("Run", () => ScanRunValues(ctx, Add));

        if (deep)
        {
            Safe("layanan", () => ScanServices(ctx, Add, ct));
            Safe("tugas terjadwal", () => ScanTasks(ctx, Add, ct));
            Safe("CLSID", () => ScanClsid(ctx, Add, ct));
            Safe("pencarian drive", () => ScanDrives(ctx, Add, ct));
        }

        var items = RemoveNested(found.Values).Select(WithSize).OrderByDescending(i => i.Score).ThenBy(i => i.Kind).ThenBy(i => i.Location, StringComparer.OrdinalIgnoreCase).ToList();
        logger.LogInformation("Pindai sisa {Program} ({Mode}) selesai dalam {Elapsed} ms: {Count} temuan",
            program.DisplayName, deep ? "Super" : "Normal", stopwatch.ElapsedMilliseconds, items.Count);
        return items;

        void Safe(string source, Action scan)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                scan();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Sumber sisa '{Source}' dilewati", source);
            }
        }
    }

    // ------------------------------------------------------------------ berkas & folder

    private void ScanInstallLocation(LeftoverContext ctx, AddItem add)
    {
        if (ctx.InstallLocation is { } folder && Directory.Exists(folder) && IsCandidatePath(ctx, folder))
        {
            add(LeftoverKind.Folder, folder, LeftoverEvidence.InstallLocation);
        }
    }

    private void ScanFolders(LeftoverContext ctx, AddItem add, CancellationToken ct)
    {
        foreach (string root in roots.FolderRoots.Where(Directory.Exists))
        {
            ScanFolderChildren(ctx, add, root, userData: false, ct);
        }

        foreach (string root in roots.UserDataRoots.Where(Directory.Exists))
        {
            ScanFolderChildren(ctx, add, root, userData: true, ct);
        }
    }

    private void ScanFolderChildren(LeftoverContext ctx, AddItem add, string root, bool userData, CancellationToken ct)
    {
        foreach (string dir in Directory.EnumerateDirectories(root, "*", TopLevel))
        {
            ct.ThrowIfCancellationRequested();
            string name = Path.GetFileName(dir);
            if (!IsCandidatePath(ctx, dir))
            {
                continue;
            }

            if (!userData && ctx.IsPublisherName(name))
            {
                // Folder penerbit: telusuri satu tingkat ke dalam (C:\Program Files\VideoLAN\VLC).
                bool allChildrenMatch = true;
                bool any = false;
                foreach (string child in Directory.EnumerateDirectories(dir, "*", TopLevel))
                {
                    any = true;
                    var childEvidence = ctx.EvidenceForName(Path.GetFileName(child), underPublisher: true);
                    if (childEvidence is { } ev && IsCandidatePath(ctx, child))
                    {
                        add(LeftoverKind.Folder, child, ev);
                    }
                    else
                    {
                        allChildrenMatch = false;
                    }
                }

                // Folder penerbit sendiri hanya bila isinya semua milik program ini dan penerbit tidak dipakai program lain.
                if (!ctx.PublisherShared && (allChildrenMatch || !any) && ctx.EvidenceForName(name, false) is { } publisherEvidence)
                {
                    add(LeftoverKind.Folder, dir, publisherEvidence);
                }

                continue;
            }

            if (ctx.EvidenceForName(name, underPublisher: false) is { } evidence)
            {
                if (userData)
                {
                    // Dokumen/profil bisa berisi karya pengguna (proyek, tangkapan layar): hanya nama persis,
                    // dan selalu Ragu (tersembunyi + konfirmasi kedua). Nama mirip tidak dilaporkan sama sekali.
                    if (evidence != LeftoverEvidence.ExactName)
                    {
                        continue;
                    }

                    evidence = LeftoverEvidence.UserData;
                }

                add(LeftoverKind.Folder, dir, evidence);
            }
        }
    }

    private void ScanShortcuts(LeftoverContext ctx, AddItem add, CancellationToken ct)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (string root in roots.ShortcutRoots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string link in Directory.EnumerateFiles(root, "*.lnk", options))
            {
                ct.ThrowIfCancellationRequested();
                if (!IsCandidatePath(ctx, link))
                {
                    continue;
                }

                string? target = ShortcutResolver.GetTarget(link);
                if (target is not null && ctx.IsInsideInstallLocation(target))
                {
                    add(LeftoverKind.Shortcut, link, LeftoverEvidence.PointsIntoInstallLocation, target);
                }
                else if (target is null && ctx.EvidenceForName(Path.GetFileNameWithoutExtension(link), false) is LeftoverEvidence.ExactName)
                {
                    // Pintasan "advertised" MSI tanpa path: hanya nama, jadi "Mungkin".
                    add(LeftoverKind.Shortcut, link, LeftoverEvidence.ExactNameElsewhere);
                }
            }
        }
    }

    private void ScanDrives(LeftoverContext ctx, AddItem add, CancellationToken ct)
    {
        // Mode Super: folder bernama persis program sampai dua tingkat di bawah akar setiap drive tetap.
        foreach (string drive in roots.DriveRoots)
        {
            foreach (string level1 in SafeDirectories(drive))
            {
                ct.ThrowIfCancellationRequested();
                if (protectedPaths.IsProtected(level1) && !IsContainerOnly(level1))
                {
                    continue;
                }

                CheckElsewhere(level1);
                foreach (string level2 in SafeDirectories(level1))
                {
                    CheckElsewhere(level2);
                }
            }
        }

        void CheckElsewhere(string dir)
        {
            // Folder bertitik (.git, .vs, .cache) dan folder tersembunyi/sistem hampir selalu milik proyek
            // atau alat lain, bukan sisa program: contoh nyata ".git" di sebuah repositori cocok dengan "Git".
            if (Path.GetFileName(dir).StartsWith('.') || IsHiddenOrSystem(dir))
            {
                return;
            }

            if (IsCandidatePath(ctx, dir)
                && NameMatcher.Compare(Path.GetFileName(dir), ctx.Names) == NameMatch.Exact
                && ctx.EvidenceForName(Path.GetFileName(dir), false) is not null)
            {
                // Folder sembarang di drive lain (mis. F:\Game Dev\Android) lebih sering milik pengguna.
                add(LeftoverKind.Folder, dir, LeftoverEvidence.UserData);
            }
        }

        // Wadah seperti C:\Users atau C:\Program Files dilindungi sendiri, tetapi isinya boleh diperiksa.
        bool IsContainerOnly(string path) => !protectedPaths.IsProtected(Path.Combine(path, "__periksa__"));
    }

    private static bool IsHiddenOrSystem(string dir)
    {
        try
        {
            return (File.GetAttributes(dir) & (FileAttributes.Hidden | FileAttributes.System)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path, "*", TopLevel).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Boleh dilaporkan: tidak dilindungi dan bukan milik program lain yang masih terpasang.</summary>
    private bool IsCandidatePath(LeftoverContext ctx, string path) =>
        !protectedPaths.IsProtected(path) && !ctx.BelongsToOtherProgram(path);

    // ------------------------------------------------------------------ registri

    private void ScanSoftwareKeys(LeftoverContext ctx, AddItem add, CancellationToken ct)
    {
        foreach (var root in roots.SoftwareRoots)
        {
            using var key = Open(root);
            if (key is null)
            {
                continue;
            }

            foreach (string name in key.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                string path = $@"{root.FullPath}\{name}";
                if (ProtectedRegistryKeys.IsProtected(path) || name.Equals("WOW6432Node", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (ctx.IsPublisherName(name))
                {
                    using var publisher = TryOpen(key, name);
                    if (publisher is null)
                    {
                        continue;
                    }

                    string[] children = publisher.GetSubKeyNames();
                    bool allMatch = true;
                    foreach (string child in children)
                    {
                        if (ctx.EvidenceForName(child, underPublisher: true) is { } ev)
                        {
                            add(LeftoverKind.RegistryKey, $@"{path}\{child}", ev);
                        }
                        else
                        {
                            allMatch = false;
                        }
                    }

                    if (!ctx.PublisherShared && allMatch && ctx.EvidenceForName(name, false) is { } publisherEvidence)
                    {
                        add(LeftoverKind.RegistryKey, path, publisherEvidence);
                    }

                    continue;
                }

                if (ctx.EvidenceForName(name, underPublisher: false) is { } evidence)
                {
                    add(LeftoverKind.RegistryKey, path, evidence);
                }
            }
        }
    }

    private static void ScanUninstallKey(InstalledProgram program, AddItem add)
    {
        if (program.RegistryKeyPath is { } key && InstallStateProbe.RegistryKeyExists(key) && !ProtectedRegistryKeys.IsProtected(key))
        {
            add(LeftoverKind.RegistryKey, key, LeftoverEvidence.UninstallKey);
        }
    }

    private void ScanAppPaths(LeftoverContext ctx, AddItem add, CancellationToken ct)
    {
        foreach (var root in roots.AppPathsKeys)
        {
            using var key = Open(root);
            if (key is null)
            {
                continue;
            }

            foreach (string name in key.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                using var entry = TryOpen(key, name);
                string? exe = entry?.GetValue(null) as string ?? entry?.GetValue("Path") as string;
                if (exe is not null && ctx.CommandPointsIntoInstallLocation(exe))
                {
                    add(LeftoverKind.RegistryKey, $@"{root.FullPath}\{name}", LeftoverEvidence.PointsIntoInstallLocation, exe);
                }
            }
        }
    }

    private void ScanRunValues(LeftoverContext ctx, AddItem add)
    {
        foreach (var root in roots.RunKeys)
        {
            using var key = Open(root);
            if (key is null)
            {
                continue;
            }

            foreach (string value in key.GetValueNames().Where(v => v.Length > 0))
            {
                if (key.GetValue(value) is string command && ctx.CommandPointsIntoInstallLocation(command))
                {
                    add(LeftoverKind.RegistryValue, root.FullPath, LeftoverEvidence.PointsIntoInstallLocation, command, value);
                }
            }
        }
    }

    private void ScanServices(LeftoverContext ctx, AddItem add, CancellationToken ct)
    {
        if (roots.ServicesKey is not { } servicesRoot)
        {
            return;
        }

        using var key = Open(servicesRoot);
        if (key is null)
        {
            return;
        }

        foreach (string name in key.GetSubKeyNames())
        {
            ct.ThrowIfCancellationRequested();
            using var serviceKey = TryOpen(key, name);
            if (serviceKey is null)
            {
                continue;
            }

            ServiceInfo info = ServiceRemover.Read(name, serviceKey);
            if (!ProtectedServices.IsProtected(info) && ctx.IsInsideInstallLocation(info.ImagePath))
            {
                add(LeftoverKind.Service, name, LeftoverEvidence.PointsIntoInstallLocation, info.ImagePath);
            }
        }
    }

    private void ScanTasks(LeftoverContext ctx, AddItem add, CancellationToken ct)
    {
        if (roots.TasksFolder is not { } folder || !Directory.Exists(folder))
        {
            return;
        }

        var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true };
        foreach (string file in Directory.EnumerateFiles(folder, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            string taskPath = @"\" + Path.GetRelativePath(folder, file);
            if (ProtectedServices.IsProtectedTask(taskPath))
            {
                continue;
            }

            foreach (string command in ReadTaskCommands(file))
            {
                if (ctx.CommandPointsIntoInstallLocation(command))
                {
                    add(LeftoverKind.ScheduledTask, taskPath, LeftoverEvidence.PointsIntoInstallLocation, command);
                    break;
                }
            }
        }
    }

    /// <summary>Isi &lt;Exec&gt;&lt;Command&gt; dari XML tugas. Berkas rusak menghasilkan daftar kosong.</summary>
    internal static IEnumerable<string> ReadTaskCommands(string file)
    {
        try
        {
            var doc = XDocument.Load(file);
            return [.. doc.Descendants().Where(e => e.Name.LocalName == "Command").Select(e => e.Value.Trim().Trim('"')).Where(c => c.Length > 0)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return [];
        }
    }

    private void ScanClsid(LeftoverContext ctx, AddItem add, CancellationToken ct)
    {
        foreach (var root in roots.ClsidKeys)
        {
            using var key = Open(root);
            if (key is null)
            {
                continue;
            }

            foreach (string clsid in key.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                using var entry = TryOpen(key, clsid);
                if (entry is null)
                {
                    continue;
                }

                foreach (string serverKey in new[] { "InprocServer32", "LocalServer32" })
                {
                    using var server = TryOpen(entry, serverKey);
                    if (server?.GetValue(null) is string serverPath && ctx.CommandPointsIntoInstallLocation(serverPath))
                    {
                        add(LeftoverKind.RegistryKey, $@"{root.FullPath}\{clsid}", LeftoverEvidence.PointsIntoInstallLocation, serverPath);
                        break;
                    }
                }
            }
        }
    }

    private static RegistryKey? Open(RegistryRoot root)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(root.Hive, RegistryView.Registry64);
            return baseKey.OpenSubKey(root.SubKey, writable: false);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static RegistryKey? TryOpen(RegistryKey parent, string name)
    {
        try
        {
            return parent.OpenSubKey(name, writable: false);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ hasil

    /// <summary>Folder/berkas di dalam folder lain yang juga ditemukan tidak ditampilkan dua kali.</summary>
    private static IEnumerable<LeftoverItem> RemoveNested(IEnumerable<LeftoverItem> items)
    {
        var list = items.ToList();
        var folders = list.Where(i => i.Kind == LeftoverKind.Folder).ToList();
        var keys = list.Where(i => i.Kind == LeftoverKind.RegistryKey).ToList();

        return list.Where(item => item.Kind switch
        {
            LeftoverKind.Folder or LeftoverKind.File or LeftoverKind.Shortcut => !folders.Any(f =>
                !ReferenceEquals(f, item) && f.Score >= item.Score && item.Location.StartsWith(f.Location + @"\", StringComparison.OrdinalIgnoreCase)),
            LeftoverKind.RegistryKey => !keys.Any(k =>
                !ReferenceEquals(k, item) && k.Score >= item.Score && item.Location.StartsWith(k.Location + @"\", StringComparison.OrdinalIgnoreCase)),
            _ => true,
        });
    }

    private LeftoverItem WithSize(LeftoverItem item) => item.Kind switch
    {
        LeftoverKind.Folder => item with { SizeBytes = sizes.Measure(item.Location) },
        LeftoverKind.File or LeftoverKind.Shortcut when File.Exists(item.Location) => item with { SizeBytes = new FileInfo(item.Location).Length },
        _ => item,
    };

    private delegate void AddItem(LeftoverKind kind, string location, LeftoverEvidence evidence, string? detail = null, string? valueName = null);
}

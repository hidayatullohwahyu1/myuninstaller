using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using MyUninstaller.Core.Leftovers;
using MyUninstaller.Core.Monitoring;
using MyUninstaller.Core.Safety;
using MyUninstaller.Infrastructure.Leftovers;

namespace MyUninstaller.Infrastructure.Monitoring;

/// <summary>
/// Gambaran sistem untuk "Pantau instalasi" (versi 2.0). Hanya membaca NAMA (folder, berkas, kunci, nilai, layanan,
/// tugas) di lokasi yang sama dengan pemindai sisa, dengan kedalaman terbatas agar cepat. Lokasi yang dilindungi
/// (Windows, Microsoft, Classes, ...) tidak dicatat.
/// </summary>
public sealed class SystemSnapshotter(
    LeftoverScanRoots roots,
    ProtectedPaths protectedPaths,
    ILogger<SystemSnapshotter> logger,
    IReadOnlyList<RegistryRoot>? uninstallRoots = null) : ISystemSnapshotter, ITraceItemProbe
{
    private const string Uninstall = @"Microsoft\Windows\CurrentVersion\Uninstall";

    // Folder yang berubah terus oleh Windows/program lain: bukan buatan installer.
    private static readonly HashSet<string> NoisyFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Temp", "CrashDumps", "D3DSCache", "Packages", "Microsoft", "WindowsApps", "Package Cache", "NVIDIA",
        "NVIDIA Corporation", "History", "Comms", "ConnectedDevicesPlatform", "PeerDistRepub", "Downloaded Installations",
    };

    // Subkunci SOFTWARE yang dilindungi atau milik Windows: tidak dicatat.
    private static readonly HashSet<string> SkippedSoftwareKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Classes", "Microsoft", "WOW6432Node", "Policies", "RegisteredApplications", "Windows", "Intel", "Wow6432Node",
    };

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private IReadOnlyList<RegistryRoot> UninstallRoots { get; } = uninstallRoots ??
    [
        new(RegistryHive.LocalMachine, $@"SOFTWARE\{Uninstall}"),
        new(RegistryHive.LocalMachine, $@"SOFTWARE\WOW6432Node\{Uninstall}"),
        new(RegistryHive.CurrentUser, $@"Software\{Uninstall}"),
    ];

    public SystemSnapshot Take(CancellationToken cancellationToken)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var items = new List<TraceItem>();

        foreach (string root in roots.FolderRoots.Concat(roots.ShortcutRoots).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            AddFolder(root, depth: 2, items, cancellationToken);
        }

        foreach (string root in roots.UserDataRoots)
        {
            AddFolder(root, depth: 1, items, cancellationToken);
        }

        foreach (var root in roots.SoftwareRoots)
        {
            AddKeys(root, depth: 2, skip: SkippedSoftwareKeys, items, cancellationToken);
        }

        foreach (var root in roots.AppPathsKeys.Concat(roots.ClsidKeys).Concat(UninstallRoots))
        {
            AddKeys(root, depth: 1, skip: null, items, cancellationToken);
        }

        foreach (var root in roots.RunKeys)
        {
            AddValues(root, items);
        }

        if (roots.ServicesKey is { } services)
        {
            using var key = Open(services.FullPath);
            foreach (string name in SafeSubKeyNames(key))
            {
                items.Add(new TraceItem(LeftoverKind.Service, name));
            }
        }

        if (roots.TasksFolder is { } tasks && Directory.Exists(tasks))
        {
            foreach (string file in Directory.EnumerateFiles(tasks, "*", new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true }))
            {
                string taskPath = @"\" + Path.GetRelativePath(tasks, file);
                if (!ProtectedServices.IsProtectedTask(taskPath))
                {
                    items.Add(new TraceItem(LeftoverKind.ScheduledTask, taskPath));
                }
            }
        }

        var snapshot = new SystemSnapshot(items);
        logger.LogInformation("Gambaran sistem: {Count} benda dalam {Ms} ms", snapshot.Count, clock.ElapsedMilliseconds);
        return snapshot;
    }

    private void AddFolder(string folder, int depth, List<TraceItem> items, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(folder))
        {
            return;
        }

        try
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*", Options))
            {
                if (!protectedPaths.IsProtected(file))
                {
                    items.Add(new TraceItem(LeftoverKind.File, file));
                }
            }

            foreach (string sub in Directory.EnumerateDirectories(folder, "*", Options))
            {
                if (NoisyFolders.Contains(Path.GetFileName(sub)) || protectedPaths.IsProtected(sub))
                {
                    continue;
                }

                items.Add(new TraceItem(LeftoverKind.Folder, sub));
                if (depth > 1)
                {
                    AddSubfolders(sub, depth - 1, items, cancellationToken);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Folder {Folder} dilewati", folder);
        }
    }

    private void AddSubfolders(string folder, int depth, List<TraceItem> items, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            foreach (string sub in Directory.EnumerateDirectories(folder, "*", Options))
            {
                if (!NoisyFolders.Contains(Path.GetFileName(sub)))
                {
                    items.Add(new TraceItem(LeftoverKind.Folder, sub));
                    if (depth > 1)
                    {
                        AddSubfolders(sub, depth - 1, items, cancellationToken);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Folder {Folder} dilewati", folder);
        }
    }

    private void AddKeys(RegistryRoot root, int depth, HashSet<string>? skip, List<TraceItem> items, CancellationToken cancellationToken)
    {
        using var key = Open(root.FullPath);
        Walk(key, root.FullPath, depth);

        void Walk(RegistryKey? parent, string path, int remaining)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string name in SafeSubKeyNames(parent))
            {
                if (skip is not null && remaining == depth && skip.Contains(name))
                {
                    continue;
                }

                string child = path + @"\" + name;
                items.Add(new TraceItem(LeftoverKind.RegistryKey, child));
                if (remaining > 1)
                {
                    try
                    {
                        using var sub = parent!.OpenSubKey(name);
                        Walk(sub, child, remaining - 1);
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                    {
                        // Kunci tanpa izin baca: dilewati.
                    }
                }
            }
        }
    }

    private static void AddValues(RegistryRoot root, List<TraceItem> items)
    {
        using var key = Open(root.FullPath);
        if (key is null)
        {
            return;
        }

        try
        {
            foreach (string name in key.GetValueNames().Where(n => n.Length > 0))
            {
                items.Add(new TraceItem(LeftoverKind.RegistryValue, root.FullPath, name));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Dilewati.
        }
    }

    public string? ReadDisplayName(string uninstallKeyPath)
    {
        using var key = Open(uninstallKeyPath);
        return key?.GetValue("DisplayName") as string;
    }

    public bool Exists(TraceItem item)
    {
        try
        {
            switch (item.Kind)
            {
                case LeftoverKind.Folder:
                    return Directory.Exists(item.Location);
                case LeftoverKind.File or LeftoverKind.Shortcut:
                    return File.Exists(item.Location);
                case LeftoverKind.RegistryKey:
                {
                    using var key = Open(item.Location);
                    return key is not null;
                }

                case LeftoverKind.RegistryValue:
                {
                    using var key = Open(item.Location);
                    return key?.GetValueNames().Contains(item.ValueName, StringComparer.OrdinalIgnoreCase) == true;
                }

                case LeftoverKind.Service:
                {
                    if (roots.ServicesKey is not { } services)
                    {
                        return false;
                    }

                    using var key = Open(services.FullPath + @"\" + item.Location);
                    return key is not null;
                }

                default:
                    return roots.TasksFolder is { } tasks && File.Exists(Path.Combine(tasks, item.Location.TrimStart('\\')));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return false;
        }
    }

    private static string[] SafeSubKeyNames(RegistryKey? key)
    {
        try
        {
            return key?.GetSubKeyNames() ?? [];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    /// <summary>Membuka "HKEY_LOCAL_MACHINE\..." / "HKEY_CURRENT_USER\..." pada view 64-bit (WOW6432Node eksplisit).</summary>
    private static RegistryKey? Open(string fullPath)
    {
        int slash = fullPath.IndexOf('\\');
        if (slash < 0)
        {
            return null;
        }

        RegistryHive? hive = fullPath[..slash].ToUpperInvariant() switch
        {
            "HKEY_LOCAL_MACHINE" or "HKLM" => RegistryHive.LocalMachine,
            "HKEY_CURRENT_USER" or "HKCU" => RegistryHive.CurrentUser,
            _ => null,
        };
        if (hive is null)
        {
            return null;
        }

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive.Value, RegistryView.Registry64);
            return baseKey.OpenSubKey(fullPath[(slash + 1)..]);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}

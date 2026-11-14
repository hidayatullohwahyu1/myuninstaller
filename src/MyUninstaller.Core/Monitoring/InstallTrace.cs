using MyUninstaller.Core.Leftovers;
using MyUninstaller.Core.Models;

namespace MyUninstaller.Core.Monitoring;

/// <summary>Satu benda di sistem yang bisa dibuat installer: folder, berkas, kunci/nilai registri, layanan, tugas.</summary>
public sealed record TraceItem(LeftoverKind Kind, string Location, string? ValueName = null)
{
    /// <summary>Kunci perbandingan (tanpa beda huruf besar/kecil, sama seperti Windows).</summary>
    public string Key => $"{Kind}|{Location.TrimEnd('\\')}|{ValueName}".ToUpperInvariant();

    public bool IsPath => Kind is LeftoverKind.Folder or LeftoverKind.File or LeftoverKind.Shortcut;
}

/// <summary>Gambaran sistem pada satu saat (hanya nama, bukan isi).</summary>
public sealed class SystemSnapshot
{
    private readonly Dictionary<string, TraceItem> _items;

    public SystemSnapshot(IEnumerable<TraceItem> items)
    {
        _items = [];
        foreach (var item in items)
        {
            _items.TryAdd(item.Key, item);
        }
    }

    public int Count => _items.Count;

    public IEnumerable<TraceItem> Items => _items.Values;

    public bool Contains(TraceItem item) => _items.ContainsKey(item.Key);
}

/// <summary>Jejak instalasi: semua yang ditambahkan installer, disimpan untuk penghapusan bersih nanti.</summary>
public sealed record InstallTrace
{
    public required string Id { get; init; }

    /// <summary>Nama program (dari entri Uninstall baru, bisa diubah pengguna).</summary>
    public required string Name { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required IReadOnlyList<TraceItem> Items { get; init; }

    /// <summary>Kunci Uninstall yang dibuat installer (untuk mengenali programnya di daftar).</summary>
    public IReadOnlyList<string> UninstallKeys { get; init; } = [];
}

/// <summary>Mengambil gambaran sistem (Infrastruktur).</summary>
public interface ISystemSnapshotter
{
    SystemSnapshot Take(CancellationToken cancellationToken);

    /// <summary>DisplayName di kunci Uninstall, atau null.</summary>
    string? ReadDisplayName(string uninstallKeyPath);
}

/// <summary>Penyimpanan jejak (berkas JSON di %ProgramData%\MyUninstaller\Jejak).</summary>
public interface IInstallTraceStore
{
    IReadOnlyList<InstallTrace> GetAll();

    void Save(InstallTrace trace);

    void Delete(string id);
}

/// <summary>Apakah benda jejak masih ada (dicek tepat saat mencari sisa).</summary>
public interface ITraceItemProbe
{
    bool Exists(TraceItem item);
}

public static class InstallTraceRules
{
    private const string UninstallSuffix = @"\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>
    /// Benda yang ditambahkan antara dua gambaran. Hanya yang paling atas disimpan: folder baru beserta isinya cukup
    /// dicatat sebagai foldernya, kunci baru beserta subkunci/nilainya cukup kuncinya.
    /// </summary>
    public static IReadOnlyList<TraceItem> Added(SystemSnapshot before, SystemSnapshot after)
    {
        var added = after.Items.Where(i => !before.Contains(i)).ToList();
        var newFolders = new HashSet<string>(added.Where(i => i.Kind == LeftoverKind.Folder).Select(i => Trim(i.Location)), StringComparer.OrdinalIgnoreCase);
        var newKeys = new HashSet<string>(added.Where(i => i.Kind == LeftoverKind.RegistryKey).Select(i => Trim(i.Location)), StringComparer.OrdinalIgnoreCase);

        return
        [
            .. added
                .Where(item => item.Kind switch
                {
                    LeftoverKind.Folder or LeftoverKind.File or LeftoverKind.Shortcut => !HasAncestor(item.Location, newFolders),
                    LeftoverKind.RegistryKey => !HasAncestor(item.Location, newKeys),
                    LeftoverKind.RegistryValue => !newKeys.Contains(Trim(item.Location)) && !HasAncestor(item.Location, newKeys),
                    _ => true,
                })
                .OrderBy(i => i.Kind)
                .ThenBy(i => i.Location, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>Kunci Uninstall baru (HKLM 64/32 atau HKCU) di antara benda jejak.</summary>
    public static IReadOnlyList<string> UninstallKeysIn(IEnumerable<TraceItem> items) =>
    [
        .. items.Where(i => i.Kind == LeftoverKind.RegistryKey
                            && Path.GetDirectoryName(Trim(i.Location)) is { } parent
                            && parent.EndsWith(UninstallSuffix, StringComparison.OrdinalIgnoreCase))
            .Select(i => Trim(i.Location)),
    ];

    /// <summary>Jejak milik program: kunci Uninstall-nya tercatat, atau namanya sama (setelah dinormalkan).</summary>
    public static bool Matches(InstallTrace trace, InstalledProgram program)
    {
        if (program.RegistryKeyPath is { } key
            && trace.UninstallKeys.Any(k => Trim(k).Equals(Trim(key), StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        string traceName = NameMatcher.Normalize(trace.Name);
        return traceName.Length >= 3 && traceName == NameMatcher.Normalize(program.DisplayName);
    }

    private static bool HasAncestor(string path, HashSet<string> ancestors)
    {
        string? parent = Path.GetDirectoryName(Trim(path));
        while (!string.IsNullOrEmpty(parent))
        {
            if (ancestors.Contains(parent))
            {
                return true;
            }

            parent = Path.GetDirectoryName(parent);
        }

        return false;
    }

    private static string Trim(string path) => path.TrimEnd('\\');
}

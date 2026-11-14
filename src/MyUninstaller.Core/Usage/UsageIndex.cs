using System.Text;

namespace MyUninstaller.Core.Usage;

/// <summary>
/// Indeks "terakhir dipakai" dari dua sumber Windows (PRD 1.2 "jarang dipakai"):
/// UserAssist (program yang dibuka pengguna lewat Explorer/menu Mulai, per path lengkap) dan
/// Prefetch (semua exe yang pernah dijalankan, per nama berkas). Hanya perkiraan; tanpa bukti = tidak diketahui.
/// </summary>
public sealed class UsageIndex(
    IReadOnlyDictionary<string, DateTimeOffset> byPath,
    IReadOnlyDictionary<string, DateTimeOffset> byPrefetchName)
{
    /// <summary>Panjang maksimum nama exe di nama berkas Prefetch ("NAMA.EXE-HASH.pf").</summary>
    public const int PrefetchNameLength = 29;

    // Exe yang berjalan tanpa pengguna memakai programnya (uninstaller, pembaru, pelapor crash).
    private static readonly string[] IgnoredPrefixes =
    [
        "unins", "uninst", "uninstall", "setup", "install", "update", "updater", "autoupdate", "crashpad", "crashreporter",
        "crashhandler", "bugreport", "elevate", "maintenanceservice", "notification_helper",
    ];

    public static UsageIndex Empty { get; } = new(new Dictionary<string, DateTimeOffset>(), new Dictionary<string, DateTimeOffset>());

    public int Count => byPath.Count + byPrefetchName.Count;

    /// <summary>Waktu terakhir salah satu exe program dijalankan, atau null bila tidak ada bukti.</summary>
    public DateTimeOffset? LastUsed(IEnumerable<string> executablePaths)
    {
        DateTimeOffset? latest = null;
        foreach (string exe in executablePaths)
        {
            string fileName = Path.GetFileName(exe);
            if (IsIgnored(fileName))
            {
                continue;
            }

            if (byPath.TryGetValue(exe, out var fromUserAssist) && (latest is null || fromUserAssist > latest))
            {
                latest = fromUserAssist;
            }

            if (byPrefetchName.TryGetValue(PrefetchKey(fileName), out var fromPrefetch) && (latest is null || fromPrefetch > latest))
            {
                latest = fromPrefetch;
            }
        }

        return latest;
    }

    public static bool IsIgnored(string exeFileName) =>
        IgnoredPrefixes.Any(p => exeFileName.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>"chrome.exe" → "CHROME.EXE" (dipotong 29 karakter seperti nama berkas Prefetch).</summary>
    public static string PrefetchKey(string exeFileName)
    {
        string upper = exeFileName.ToUpperInvariant();
        return upper.Length > PrefetchNameLength ? upper[..PrefetchNameLength] : upper;
    }

    /// <summary>"CHROME.EXE-ABCD1234.pf" → "CHROME.EXE"; null bila bukan nama Prefetch.</summary>
    public static string? PrefetchKeyFromFile(string prefetchFileName)
    {
        string name = Path.GetFileNameWithoutExtension(prefetchFileName);
        int dash = name.LastIndexOf('-');
        return dash > 0 ? PrefetchKey(name[..dash]) : null;
    }

    /// <summary>Nama nilai UserAssist disandikan ROT13.</summary>
    public static string Rot13(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            builder.Append(c switch
            {
                >= 'a' and <= 'z' => (char)('a' + ((c - 'a' + 13) % 26)),
                >= 'A' and <= 'Z' => (char)('A' + ((c - 'A' + 13) % 26)),
                _ => c,
            });
        }

        return builder.ToString();
    }

    /// <summary>
    /// Data UserAssist (Windows 7+, 72 bita): FILETIME terakhir dijalankan di offset 60. Null bila tidak ada.
    /// </summary>
    public static DateTimeOffset? UserAssistLastRun(byte[]? data)
    {
        if (data is not { Length: >= 68 })
        {
            return null;
        }

        long fileTime = BitConverter.ToInt64(data, 60);
        if (fileTime <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromFileTime(fileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}

/// <summary>Membangun indeks pemakaian dan mencari exe program (hanya baca).</summary>
public interface IUsageHistory
{
    UsageIndex Load();

    /// <summary>Exe di folder instalasi (dua tingkat, dibatasi jumlahnya).</summary>
    IReadOnlyList<string> ExecutablesIn(string installLocation);
}

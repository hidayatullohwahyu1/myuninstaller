using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using MyUninstaller.Core.Usage;

namespace MyUninstaller.Infrastructure.Usage;

/// <summary>
/// Membaca riwayat pemakaian dari Prefetch (C:\Windows\Prefetch, butuh admin) dan UserAssist (HKCU). Hanya baca.
/// </summary>
public sealed partial class UsageHistoryReader(ILogger<UsageHistoryReader> logger) : IUsageHistory
{
    private const string UserAssistKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist";
    private const int MaxExecutables = 300;

    public UsageIndex Load()
    {
        var byPath = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var byPrefetch = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        try
        {
            ReadUserAssist(byPath);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            logger.LogDebug(ex, "UserAssist tidak bisa dibaca");
        }

        try
        {
            ReadPrefetch(byPrefetch);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Normal bila aplikasi tidak berjalan sebagai admin (mode pengembangan).
            logger.LogDebug(ex, "Prefetch tidak bisa dibaca");
        }

        logger.LogInformation("Riwayat pemakaian: {UserAssist} entri UserAssist, {Prefetch} entri Prefetch", byPath.Count, byPrefetch.Count);
        return new UsageIndex(byPath, byPrefetch);
    }

    public IReadOnlyList<string> ExecutablesIn(string installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation) || !Directory.Exists(installLocation))
        {
            return [];
        }

        var result = new List<string>();
        try
        {
            AddExes(installLocation, result);
            foreach (string sub in Directory.EnumerateDirectories(installLocation, "*", new EnumerationOptions
                     {
                         IgnoreInaccessible = true,
                         AttributesToSkip = FileAttributes.ReparsePoint,
                     }))
            {
                if (result.Count >= MaxExecutables)
                {
                    break;
                }

                AddExes(sub, result);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Exe di {Folder} tidak bisa dibaca", installLocation);
        }

        return result;

        static void AddExes(string folder, List<string> list)
        {
            foreach (string exe in Directory.EnumerateFiles(folder, "*.exe", new EnumerationOptions { IgnoreInaccessible = true }))
            {
                if (list.Count >= MaxExecutables)
                {
                    return;
                }

                list.Add(exe);
            }
        }
    }

    private static void ReadPrefetch(Dictionary<string, DateTimeOffset> byPrefetch)
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*.pf"))
        {
            if (UsageIndex.PrefetchKeyFromFile(file.Name) is not { } key)
            {
                continue;
            }

            var time = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
            if (!byPrefetch.TryGetValue(key, out var existing) || time > existing)
            {
                byPrefetch[key] = time;
            }
        }
    }

    private static void ReadUserAssist(Dictionary<string, DateTimeOffset> byPath)
    {
        using var root = Registry.CurrentUser.OpenSubKey(UserAssistKey, writable: false);
        if (root is null)
        {
            return;
        }

        foreach (string guid in root.GetSubKeyNames())
        {
            using var count = root.OpenSubKey($@"{guid}\Count", writable: false);
            if (count is null)
            {
                continue;
            }

            foreach (string encoded in count.GetValueNames())
            {
                if (UsageIndex.UserAssistLastRun(count.GetValue(encoded) as byte[]) is not { } time
                    || ResolvePath(UsageIndex.Rot13(encoded)) is not { } path)
                {
                    continue;
                }

                if (!byPath.TryGetValue(path, out var existing) || time > existing)
                {
                    byPath[path] = time;
                }
            }
        }
    }

    /// <summary>"{6D809377-...}\VideoLAN\VLC\vlc.exe" → "C:\Program Files\VideoLAN\VLC\vlc.exe".</summary>
    private static string? ResolvePath(string decoded)
    {
        if (!decoded.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (Path.IsPathFullyQualified(decoded))
        {
            return decoded;
        }

        if (decoded.StartsWith('{') && decoded.IndexOf('}') is var close and > 0
            && Guid.TryParse(decoded[..(close + 1)], out Guid folderId)
            && KnownFolderPath(folderId) is { } basePath)
        {
            return Path.Combine(basePath, decoded[(close + 1)..].TrimStart('\\'));
        }

        return null;
    }

    private static string? KnownFolderPath(Guid id)
    {
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out IntPtr path) != 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(path);
        }
        finally
        {
            Marshal.FreeCoTaskMem(path);
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid rfid, uint flags, IntPtr token, out IntPtr path);
}

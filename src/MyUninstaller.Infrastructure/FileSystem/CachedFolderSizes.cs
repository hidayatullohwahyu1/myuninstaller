using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MyUninstaller.Core.Abstractions;

namespace MyUninstaller.Infrastructure.FileSystem;

/// <summary>Ukuran folder dengan cache JSON per pengguna (%LocalAppData%\MyUninstaller\ukuran-folder.json).</summary>
public sealed class CachedFolderSizes(IFolderSizeCalculator inner, IClock clock, string cacheFile, ILogger<CachedFolderSizes> logger)
    : ICachedFolderSizes
{
    /// <summary>Batas jumlah entri agar berkas cache tidak tumbuh tanpa batas.</summary>
    private const int MaxEntries = 2_000;

    private readonly Lazy<ConcurrentDictionary<string, FolderSizeEntry>> _entries = new(() => Load(cacheFile, logger));
    private int _changes;

    public long? Measure(string path, CancellationToken cancellationToken = default)
    {
        DateTime writeTime;
        string key;
        try
        {
            if (!Directory.Exists(path))
            {
                return null;
            }

            writeTime = Directory.GetLastWriteTimeUtc(path);
            key = FolderSizeCacheRules.Key(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        DateTime now = clock.Now.UtcDateTime;
        if (_entries.Value.TryGetValue(key, out var cached) && FolderSizeCacheRules.IsValid(cached, writeTime, now))
        {
            return cached.Bytes;
        }

        long? size = inner.Measure(path, cancellationToken);
        if (size is { } bytes)
        {
            _entries.Value[key] = new FolderSizeEntry(bytes, writeTime, now);
            Interlocked.Increment(ref _changes);
        }

        return size;
    }

    public void Save()
    {
        if (Interlocked.Exchange(ref _changes, 0) == 0)
        {
            return;
        }

        try
        {
            var newest = _entries.Value
                .OrderByDescending(e => e.Value.MeasuredAtUtc)
                .Take(MaxEntries)
                .ToDictionary(e => e.Key, e => e.Value);
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            string temp = cacheFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(newest));
            File.Move(temp, cacheFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cache hanya mempercepat; gagal simpan tidak mengganggu.
            logger.LogDebug(ex, "Cache ukuran folder tidak tersimpan");
        }
    }

    private static ConcurrentDictionary<string, FolderSizeEntry> Load(string file, ILogger logger)
    {
        try
        {
            if (File.Exists(file)
                && JsonSerializer.Deserialize<Dictionary<string, FolderSizeEntry>>(File.ReadAllText(file)) is { } saved)
            {
                return new ConcurrentDictionary<string, FolderSizeEntry>(saved);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogDebug(ex, "Cache ukuran folder rusak; dihitung ulang");
        }

        return new ConcurrentDictionary<string, FolderSizeEntry>();
    }
}

namespace MyUninstaller.Core.Abstractions;

/// <summary>Satu ukuran folder yang pernah dihitung.</summary>
/// <param name="Bytes">Ukuran total.</param>
/// <param name="FolderWriteTimeUtc">Waktu ubah folder saat dihitung (berubah bila isi langsungnya berubah).</param>
/// <param name="MeasuredAtUtc">Kapan dihitung.</param>
public sealed record FolderSizeEntry(long Bytes, DateTime FolderWriteTimeUtc, DateTime MeasuredAtUtc);

/// <summary>
/// Ukuran folder untuk daftar program, disimpan antar-sesi agar folder besar tidak ditelusuri ulang setiap kali
/// aplikasi dibuka. HANYA untuk tampilan daftar; pemindai sisa selalu menghitung langsung.
/// </summary>
public interface ICachedFolderSizes
{
    long? Measure(string path, CancellationToken cancellationToken = default);

    /// <summary>Menyimpan cache ke disk (dipanggil setelah semua ukuran selesai dihitung).</summary>
    void Save();
}

public static class FolderSizeCacheRules
{
    /// <summary>Cache dihitung ulang paling lambat setelah 7 hari meski folder tampak tidak berubah.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    /// <summary>Dipakai bila waktu ubah folder sama persis dan umurnya belum lewat <see cref="MaxAge"/>.</summary>
    public static bool IsValid(FolderSizeEntry entry, DateTime folderWriteTimeUtc, DateTime nowUtc) =>
        entry.FolderWriteTimeUtc == folderWriteTimeUtc
        && nowUtc - entry.MeasuredAtUtc < MaxAge
        && entry.MeasuredAtUtc <= nowUtc;

    /// <summary>Kunci cache: path lengkap tanpa garis miring di ujung, huruf besar (Windows tidak membedakan).</summary>
    public static string Key(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant();
}

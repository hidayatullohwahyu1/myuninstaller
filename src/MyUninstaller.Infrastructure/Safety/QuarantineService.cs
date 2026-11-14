using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MyUninstaller.Core.Safety;

namespace MyUninstaller.Infrastructure.Safety;

/// <summary>
/// Karantina: berkas/folder DIPINDAH ke %ProgramData%\MyUninstaller\Karantina\&lt;sesi&gt;\&lt;n&gt;\&lt;nama&gt;,
/// tidak dihapus. Antar-drive: salin lengkap dulu, baru sumbernya dihapus. Berkas yang terkunci
/// dijadwalkan dihapus saat mulai ulang (MoveFileEx) setelah salinannya aman di karantina.
/// </summary>
public sealed partial class QuarantineService(
    string quarantineRoot,
    ProtectedPaths protectedPaths,
    ILogger<QuarantineService> logger) : IQuarantineService
{
    private const string OriginFileName = "asal.txt";
    private readonly Lock _gate = new();

    public QuarantineService(AppPaths paths, ProtectedPaths protectedPaths, ILogger<QuarantineService> logger)
        : this(paths.QuarantineDirectory, protectedPaths, logger)
    {
    }

    public string Root => quarantineRoot;

    public QuarantineResult Quarantine(string sessionId, string path, CancellationToken cancellationToken = default)
    {
        // Dicek ulang di sini juga: lapisan pertahanan terakhir.
        if (protectedPaths.IsProtected(path))
        {
            return new QuarantineResult(QuarantineStatus.Skipped, null, 0, "Dilindungi daftar lindung");
        }

        bool isDirectory = Directory.Exists(path);
        if (!isDirectory && !File.Exists(path))
        {
            return new QuarantineResult(QuarantineStatus.Skipped, null, 0, "Tidak ditemukan");
        }

        if (IsReparsePoint(path))
        {
            // Junction/symlink tidak diikuti dan tidak dipindah: isinya milik lokasi lain.
            return new QuarantineResult(QuarantineStatus.Skipped, null, 0, "Tautan simbolik/junction dilewati");
        }

        string slot = CreateSlot(sessionId, path);
        string destination = Path.Combine(slot, Path.GetFileName(Path.TrimEndingDirectorySeparator(path)));
        long bytes = isDirectory ? MeasureDirectory(path) : new FileInfo(path).Length;

        try
        {
            // 1) Pindah biasa (drive sama, tidak ada yang terkunci): instan dan atomik.
            if (SameVolume(path, slot))
            {
                try
                {
                    if (isDirectory)
                    {
                        Directory.Move(path, destination);
                    }
                    else
                    {
                        File.Move(path, destination);
                    }

                    logger.LogInformation("Karantina: {Path} → {Destination}", path, destination);
                    return new QuarantineResult(QuarantineStatus.Moved, destination, bytes);
                }
                catch (IOException ex)
                {
                    logger.LogInformation("Pindah langsung gagal ({Message}); pakai salin lalu hapus", ex.Message);
                }
            }

            // 2) Salin lengkap ke karantina dulu. Bila gagal di tengah, sumber tidak disentuh.
            cancellationToken.ThrowIfCancellationRequested();
            if (isDirectory)
            {
                CopyDirectory(path, destination, cancellationToken);
            }
            else
            {
                File.Copy(path, destination, overwrite: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // Gagal saat menyalin: sumber BELUM disentuh, jadi salinan setengah jadi aman dibuang.
            logger.LogWarning(ex, "Karantina gagal untuk {Path}; sumber dibiarkan utuh", path);
            TryDeleteDirectory(slot);
            if (ex is OperationCanceledException)
            {
                throw;
            }

            return new QuarantineResult(QuarantineStatus.Failed, null, 0, ex.Message);
        }

        // 3) Salinan lengkap sudah aman di karantina. Mulai sini salinan TIDAK PERNAH dihapus lagi,
        //    apa pun yang terjadi pada sumber. Berkas terkunci dijadwalkan dihapus saat mulai ulang;
        //    yang tidak bisa dihapus maupun dijadwalkan dibiarkan di tempat.
        var tally = new DeleteTally();
        if (isDirectory)
        {
            DeleteDirectorySource(path, tally);
        }
        else
        {
            DeleteFileSource(path, tally);
        }

        var status = tally.Scheduled > 0 ? QuarantineStatus.PendingReboot : QuarantineStatus.Moved;
        string? detail = (tally.Scheduled, tally.Left) switch
        {
            (0, 0) => null,
            (_, 0) => $"{tally.Scheduled} item dihapus saat mulai ulang",
            _ => $"{tally.Scheduled} item dihapus saat mulai ulang, {tally.Left} item tetap di tempat (salinannya ada di karantina)",
        };
        logger.LogInformation("Karantina (salin): {Path} → {Destination}; {Detail}", path, destination, detail ?? "selesai");
        return new QuarantineResult(status, destination, bytes, detail);
    }

    private sealed class DeleteTally
    {
        public int Scheduled { get; set; }

        public int Left { get; set; }
    }


    public bool Restore(string quarantinePath, string originalPath)
    {
        bool isDirectory = Directory.Exists(quarantinePath);
        if (!isDirectory && !File.Exists(quarantinePath))
        {
            logger.LogWarning("Isi karantina tidak ditemukan: {Path}", quarantinePath);
            return false;
        }

        if (Directory.Exists(originalPath) || File.Exists(originalPath))
        {
            // Jangan menimpa: program mungkin sudah dipasang ulang.
            logger.LogWarning("Lokasi asal sudah terisi, tidak dipulihkan: {Path}", originalPath);
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(originalPath)!);
        if (SameVolume(quarantinePath, originalPath))
        {
            if (isDirectory)
            {
                Directory.Move(quarantinePath, originalPath);
            }
            else
            {
                File.Move(quarantinePath, originalPath);
            }
        }
        else
        {
            if (isDirectory)
            {
                CopyDirectory(quarantinePath, originalPath, CancellationToken.None);
                ForceDeleteDirectory(quarantinePath);
            }
            else
            {
                File.Copy(quarantinePath, originalPath);
                File.Delete(quarantinePath);
            }
        }

        logger.LogInformation("Dipulihkan dari karantina: {Path}", originalPath);
        return true;
    }

    public int PurgeExpired(int retentionDays)
    {
        if (retentionDays <= 0 || !Directory.Exists(quarantineRoot))
        {
            return 0;
        }

        DateTime cutoff = DateTime.Today.AddDays(-retentionDays);
        int purged = 0;
        foreach (string session in Directory.EnumerateDirectories(quarantineRoot))
        {
            string name = Path.GetFileName(session);
            if (name.Length < 8 || !DateTime.TryParseExact(name[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
            {
                continue; // Bukan folder sesi buatan aplikasi ini.
            }

            if (date < cutoff)
            {
                try
                {
                    ForceDeleteDirectory(session);
                    purged++;
                    logger.LogInformation("Karantina kedaluwarsa dikosongkan: {Session}", name);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Gagal mengosongkan karantina {Session}", name);
                }
            }
        }

        return purged;
    }

    public string SessionFolder(string sessionId) => Path.Combine(quarantineRoot, ValidateSessionId(sessionId));

    public bool PurgeSession(string sessionId)
    {
        string folder = SessionFolder(sessionId);
        if (!Directory.Exists(folder))
        {
            return true;
        }

        try
        {
            ForceDeleteDirectory(folder);
            logger.LogInformation("Karantina sesi {Session} dikosongkan atas permintaan pengguna", sessionId);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Gagal mengosongkan karantina sesi {Session}", sessionId);
            return false;
        }
    }

    /// <summary>ID sesi hanya boleh satu nama folder langsung di bawah akar karantina (tanpa "..", "\" atau ":").</summary>
    private static string ValidateSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)
            || sessionId.Contains("..", StringComparison.Ordinal)
            || sessionId.IndexOfAny(['\\', '/', ':']) >= 0
            || sessionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"ID sesi tidak sah: {sessionId}", nameof(sessionId));
        }

        return sessionId;
    }

    // ------------------------------------------------------------------ bantuan

    private string CreateSlot(string sessionId, string originalPath)
    {
        lock (_gate)
        {
            string sessionFolder = Path.Combine(quarantineRoot, sessionId);
            Directory.CreateDirectory(sessionFolder);
            int index = 1;
            while (Directory.Exists(Path.Combine(sessionFolder, index.ToString(CultureInfo.InvariantCulture))))
            {
                index++;
            }

            string slot = Path.Combine(sessionFolder, index.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(slot);

            // Catatan lokasi asal, untuk pemulihan manual bila basis data riwayat hilang.
            File.WriteAllText(Path.Combine(slot, OriginFileName), originalPath);
            return slot;
        }
    }

    private static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static long MeasureDirectory(string path)
    {
        long total = 0;
        foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions
                 {
                     RecurseSubdirectories = true,
                     IgnoreInaccessible = true,
                     AttributesToSkip = FileAttributes.ReparsePoint,
                 }))
        {
            total += file.Length;
        }

        return total;
    }

    /// <summary>Menyalin pohon folder tanpa mengikuti junction/symlink.</summary>
    private static void CopyDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            string target = Path.Combine(destination, entry.Name);
            if (entry is DirectoryInfo dir)
            {
                CopyDirectory(dir.FullName, target, cancellationToken);
            }
            else
            {
                File.Copy(entry.FullName, target, overwrite: false);
                File.SetAttributes(target, entry.Attributes & ~FileAttributes.ReadOnly);
            }
        }
    }

    private static void DeleteFileSource(string file, DeleteTally tally)
    {
        try
        {
            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (ScheduleDeleteOnReboot(file))
            {
                tally.Scheduled++;
            }
            else
            {
                tally.Left++;
            }
        }
    }

    /// <summary>Hapus isi sumber dari bawah ke atas. Junction tidak diikuti (hanya tautannya yang dihapus).</summary>
    private static void DeleteDirectorySource(string directory, DeleteTally tally)
    {
        List<FileSystemInfo> entries;
        try
        {
            entries = [.. new DirectoryInfo(directory).EnumerateFileSystemInfos()];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            tally.Left++;
            return;
        }

        foreach (var entry in entries)
        {
            if (entry is DirectoryInfo dir && (dir.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                DeleteDirectorySource(dir.FullName, tally);
            }
            else if (entry is DirectoryInfo)
            {
                // Junction/symlink di dalam folder: biarkan (tidak disalin, jadi juga tidak dihapus).
                tally.Left++;
            }
            else
            {
                DeleteFileSource(entry.FullName, tally);
            }
        }

        try
        {
            Directory.Delete(directory, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Folder belum kosong (ada yang terkunci/tertinggal) atau sedang dipakai.
            if (ScheduleDeleteOnReboot(directory))
            {
                tally.Scheduled++;
            }
        }
    }

    private static void ForceDeleteDirectory(string directory)
    {
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(directory, recursive: true);
    }

    private void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                ForceDeleteDirectory(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Gagal membersihkan slot karantina {Slot}", directory);
        }
    }

    private const uint MoveFileDelayUntilReboot = 0x4;

    private static bool ScheduleDeleteOnReboot(string path) => MoveFileExW(path, null, MoveFileDelayUntilReboot);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileExW(string existingFileName, string? newFileName, uint flags);
}

using System.IO.Enumeration;
using MyUninstaller.Core.Abstractions;

namespace MyUninstaller.Infrastructure.FileSystem;

/// <summary>
/// Menjumlahkan ukuran berkas di dalam folder. Junction/symlink dilewati (mencegah putaran tak berujung),
/// folder yang tidak bisa dibaca dilewati, dan penghitungan berhenti bila berkas terlalu banyak.
/// </summary>
public sealed class FolderSizeCalculator : IFolderSizeCalculator
{
    /// <summary>Batas jumlah berkas; di atas ini ukuran dianggap tidak diketahui agar tidak lama.</summary>
    public const int MaxFiles = 300_000;

    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline,
        ReturnSpecialDirectories = false,
    };

    public long? Measure(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return null;
            }

            var files = new FileSystemEnumerable<long>(
                path,
                (ref FileSystemEntry entry) => entry.Length,
                Options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory,
            };

            long total = 0;
            int count = 0;
            foreach (long length in files)
            {
                if (++count > MaxFiles)
                {
                    return null;
                }

                if ((count & 0x3FF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                total += length;
            }

            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}

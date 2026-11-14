using System.Collections.Concurrent;
using System.Text;
using MyUninstaller.Core.Uninstall;

namespace MyUninstaller.Infrastructure.Processes;

/// <summary>
/// Mengenali penghapus instalan NSIS/Inno Setup dari penanda di dalam exe (manifest dan stub), tanpa menjalankannya.
/// Hanya membaca maksimal <see cref="MaxBytes"/> pertama; hasil disimpan per path.
/// </summary>
public sealed class InstallerKindDetector : IInstallerKindDetector
{
    /// <summary>Uninstaller NSIS/Inno berukuran ratusan KB; batas ini mencegah membaca berkas besar.</summary>
    public const int MaxBytes = 8 * 1024 * 1024;

    private static readonly (byte[] Marker, InstallerKind Kind)[] Markers =
    [
        (Encoding.ASCII.GetBytes("Nullsoft.NSIS"), InstallerKind.Nsis),
        (Encoding.ASCII.GetBytes("NullsoftInst"), InstallerKind.Nsis),
        (Encoding.ASCII.GetBytes("JR.Inno.Setup"), InstallerKind.InnoSetup),
        (Encoding.ASCII.GetBytes("Inno Setup Setup Data"), InstallerKind.InnoSetup),
    ];

    private readonly ConcurrentDictionary<string, InstallerKind> _cache = new(StringComparer.OrdinalIgnoreCase);

    public InstallerKind Detect(string executablePath) => _cache.GetOrAdd(executablePath, Read);

    internal static InstallerKind Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            int length = (int)Math.Min(stream.Length, MaxBytes);
            byte[] buffer = new byte[length];
            stream.ReadExactly(buffer);

            // Harus berkas PE ("MZ"); selain itu jangan menebak.
            if (length < 2 || buffer[0] != 'M' || buffer[1] != 'Z')
            {
                return InstallerKind.Unknown;
            }

            ReadOnlySpan<byte> span = buffer;
            foreach (var (marker, kind) in Markers)
            {
                if (span.IndexOf(marker) >= 0)
                {
                    return kind;
                }
            }

            return InstallerKind.Unknown;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return InstallerKind.Unknown;
        }
    }
}

namespace MyUninstaller.Core.Shell;

/// <summary>
/// Permintaan dari menu klik kanan Explorer: <c>MyUninstaller.exe --hapus "C:\...\program.exe"</c>.
/// Nilainya datang dari luar (baris perintah atau pipe antar-instance), jadi selalu divalidasi.
/// </summary>
public static class ShellRequest
{
    public const string UninstallSwitch = "--hapus";

    /// <summary>Panjang path maksimum yang diterima (path panjang Windows).</summary>
    public const int MaxPathLength = 32_767;

    private static readonly string[] AllowedExtensions = [".exe", ".lnk"];

    /// <summary>Mengambil berkas target dari argumen baris perintah; null bila tidak ada atau tidak sah.</summary>
    public static string? ParseArguments(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (args[i].Equals(UninstallSwitch, StringComparison.OrdinalIgnoreCase))
            {
                return Validate(args[i + 1]);
            }
        }

        return null;
    }

    /// <summary>Path harus lengkap (bukan relatif/UNC perangkat), berakhiran .exe atau .lnk, tanpa karakter kendali.</summary>
    public static string? Validate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaxPathLength)
        {
            return null;
        }

        string trimmed = path.Trim().Trim('"');
        if (trimmed.Any(char.IsControl)
            || trimmed.StartsWith(@"\\?\", StringComparison.Ordinal)
            || trimmed.StartsWith(@"\\.\", StringComparison.Ordinal)
            || !Path.IsPathFullyQualified(trimmed)
            || trimmed.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return null;
        }

        string extension = Path.GetExtension(trimmed);
        return AllowedExtensions.Any(e => e.Equals(extension, StringComparison.OrdinalIgnoreCase))
            ? Path.GetFullPath(trimmed)
            : null;
    }
}

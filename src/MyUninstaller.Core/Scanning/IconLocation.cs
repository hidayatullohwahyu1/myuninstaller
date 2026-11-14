using System.Globalization;

namespace MyUninstaller.Core.Scanning;

/// <summary>Lokasi ikon: berkas dan indeks sumber daya (negatif = ID sumber daya).</summary>
public sealed record IconLocation(string Path, int Index)
{
    private static readonly string[] IconFileExtensions = [".exe", ".dll", ".ico", ".png", ".icl", ".cpl", ".ocx", ".mun"];

    /// <summary>
    /// Mengurai nilai DisplayIcon seperti <c>"C:\App\app.exe",0</c>, <c>C:\App\app.exe,-101</c>,
    /// <c>%SystemRoot%\system32\shell32.dll,3</c>, atau <c>C:\App\app.ico</c>.
    /// </summary>
    public static IconLocation? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string text = value.Trim();
        string path;
        int index = 0;

        if (text.StartsWith('"'))
        {
            int close = text.IndexOf('"', 1);
            if (close < 0)
            {
                path = text.Trim('"');
            }
            else
            {
                path = text[1..close];
                string rest = text[(close + 1)..].Trim();
                if (rest.StartsWith(','))
                {
                    _ = int.TryParse(rest[1..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out index);
                }
            }
        }
        else
        {
            int comma = text.LastIndexOf(',');
            if (comma > 0 && int.TryParse(text[(comma + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                path = text[..comma].Trim();
                index = parsed;
            }
            else
            {
                path = text;
            }
        }

        path = Environment.ExpandEnvironmentVariables(path.Trim());
        if (path.Length == 0)
        {
            return null;
        }

        return new IconLocation(path, index);
    }

    /// <summary>
    /// Ambil berkas .exe dari baris perintah UninstallString, misalnya <c>"C:\App\unins000.exe" /SILENT</c>.
    /// Mengembalikan null untuk msiexec dan rundll32 karena ikonnya bukan ikon program.
    /// </summary>
    public static string? ExecutableFromCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        string text = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        string exe;
        if (text.StartsWith('"'))
        {
            int close = text.IndexOf('"', 1);
            exe = close < 0 ? text.Trim('"') : text[1..close];
        }
        else
        {
            int end = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            exe = end < 0 ? text.Split(' ')[0] : text[..(end + 4)];
        }

        string fileName = System.IO.Path.GetFileName(exe);
        if (fileName.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("MsiExec", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe : null;
    }

    /// <summary>True bila ekstensi berkas biasa mengandung ikon.</summary>
    public bool HasIconExtension =>
        IconFileExtensions.Contains(System.IO.Path.GetExtension(Path), StringComparer.OrdinalIgnoreCase);
}

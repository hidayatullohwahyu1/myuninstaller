using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MyUninstaller.Core.Models;

namespace MyUninstaller.Core.Leftovers;

/// <summary>Seberapa cocok sebuah nama folder/kunci dengan program.</summary>
public enum NameMatch
{
    None,
    Similar,
    Exact,
}

/// <summary>
/// Pencocokan nama folder/kunci dengan nama program. Versi, arsitektur ("x64", "64-bit"), dan tanda baca
/// dibuang dulu. Nama umum (misalnya "Common", "Update", "Data") tidak pernah dianggap cocok.
/// </summary>
public static partial class NameMatcher
{
    /// <summary>Nama terlalu umum untuk dijadikan bukti (dibandingkan setelah dinormalisasi).</summary>
    private static readonly HashSet<string> GenericNames = new(StringComparer.Ordinal)
    {
        "", "app", "apps", "application", "applications", "bin", "cache", "caches", "common", "commonfiles", "config",
        "data", "default", "desktop", "documents", "download", "downloads", "driver", "drivers", "files", "help",
        "install", "installer", "lib", "local", "locallow", "log", "logs", "microsoft", "package", "packages",
        "plugins", "program", "programs", "programfiles", "programdata", "roaming", "runtime", "runtimes", "service",
        "services", "settings", "setup", "shared", "software", "system", "temp", "tmp", "tool", "tools", "update",
        "updater", "updates", "user", "users", "windows", "inc", "llc", "ltd", "corp", "corporation", "company",
        "gmbh", "team", "software", "studio", "player", "client", "server", "launcher", "assistant", "manager",
        "driverpack", "browser", "editor", "viewer", "reader", "free", "pro",
    };

    /// <summary>
    /// Bentuk dasar untuk dibandingkan: huruf kecil tanpa aksen, tanpa versi/arsitektur/tanda baca.
    /// Contoh: "7-Zip 24.09 (x64)" → "7zip", "Notepad++ (64-bit x64)" → "notepad".
    /// </summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        string text = name.ToLowerInvariant();
        text = ArchitectureRegex().Replace(text, " ");
        text = VersionRegex().Replace(text, " ");

        var builder = new StringBuilder(text.Length);
        foreach (char c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>Nama dasar program yang dipakai untuk mencocokkan (nama tampil dan nama folder instalasi).</summary>
    public static IReadOnlyList<string> ProgramNames(InstalledProgram program)
    {
        var names = new List<string>();
        Add(names, program.DisplayName);

        // Nama tanpa awalan penerbit: "Brother iPrint&Scan" → "iPrint&Scan", "Google Chrome" → "Chrome".
        // SENGAJA tidak memakai kata pertama saja: "Android Studio" → "Android" akan menangkap folder SDK,
        // "GitHub Desktop" → "GitHub" akan menangkap folder repositori pengguna.
        string[] words = program.DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string publisherFirst = Normalize(program.Publisher?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault());
        if (words.Length > 1 && publisherFirst.Length >= 3 && Normalize(words[0]) == publisherFirst)
        {
            Add(names, string.Join(' ', words[1..]));
        }

        if (!string.IsNullOrWhiteSpace(program.InstallLocation))
        {
            Add(names, Path.GetFileName(Path.TrimEndingDirectorySeparator(program.InstallLocation)));
        }

        return names;

        static void Add(List<string> list, string? value)
        {
            string n = Normalize(value);
            if (!IsGeneric(n) && !list.Contains(n))
            {
                list.Add(n);
            }
        }
    }

    public static bool IsGeneric(string normalized) => normalized.Length < 3 || GenericNames.Contains(normalized);

    /// <summary>Membandingkan nama kandidat (folder/kunci) dengan nama-nama program.</summary>
    public static NameMatch Compare(string? candidate, IReadOnlyList<string> programNames)
    {
        string c = Normalize(candidate);
        if (IsGeneric(c))
        {
            return NameMatch.None;
        }

        if (programNames.Contains(c))
        {
            return NameMatch.Exact;
        }

        // Mirip: nama kandidat MEMUAT nama program ("AudacityBackup" ⊃ "audacity"). Arah sebaliknya tidak dipakai:
        // folder pendek seperti "Epson" atau "Android" ada di dalam "EpsonScan2"/"AndroidStudio" tetapi biasanya
        // milik banyak program (driver, SDK).
        foreach (string name in programNames)
        {
            if (name.Length >= 5 && c.Length > name.Length && c.Contains(name, StringComparison.Ordinal))
            {
                return NameMatch.Similar;
            }
        }

        return NameMatch.None;
    }

    /// <summary>Nama penerbit tanpa bentuk badan usaha: "Google LLC" → "google".</summary>
    public static string NormalizePublisher(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher))
        {
            return string.Empty;
        }

        string text = CompanySuffixRegex().Replace(publisher, " ");
        string n = Normalize(text);
        return IsGeneric(n) ? string.Empty : n;
    }

    [GeneratedRegex(@"\b(x64|x86|amd64|arm64|win64|win32|64[- ]?bit|32[- ]?bit|edition)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ArchitectureRegex();

    // Versi juga dibuang bila menempel di nama: "AndroidStudio2025.3.1" → "androidstudio".
    [GeneratedRegex(@"v?\d+([.\-_]\d+)+[a-z]?\b|\bv\d+\b|\(\s*\)|\b\d{4,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"\b(inc|llc|ltd|limited|corp|corporation|co|company|gmbh|ag|sa|srl|bv|pty|plc|s\.a\.|a/s|ab|oy|kk|pte|tbk|pt)\b\.?|[,.]", RegexOptions.IgnoreCase)]
    private static partial Regex CompanySuffixRegex();
}

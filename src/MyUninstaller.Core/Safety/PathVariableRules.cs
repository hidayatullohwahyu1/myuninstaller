namespace MyUninstaller.Core.Safety;

/// <summary>Aturan murni untuk membuang satu entri dari variabel PATH pengguna (tanpa mengubah entri lain).</summary>
public static class PathVariableRules
{
    public const string ValueName = "Path";

    /// <summary>Entri apa adanya (kosong dibuang; urutan dipertahankan).</summary>
    public static IReadOnlyList<string> Split(string? value) =>
        string.IsNullOrEmpty(value) ? [] : [.. value.Split(';').Where(e => e.Trim().Length > 0)];

    /// <summary>Memperluas %VAR% lalu menyeragamkan (tanpa kutip dan garis miring di ujung) untuk dibandingkan.</summary>
    public static string Expand(string entry)
    {
        string text = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));
        return text.Length > 3 ? text.TrimEnd('\\', '/') : text;
    }

    public static bool SameEntry(string a, string b) => Expand(a).Equals(Expand(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Nilai PATH baru tanpa <paramref name="entry"/> (semua kemunculannya), atau null bila entri itu tidak ada.
    /// Entri lain, termasuk penulisan %VAR%-nya, tidak berubah.
    /// </summary>
    public static string? Without(string value, string entry)
    {
        var entries = value.Split(';');
        var kept = entries.Where(e => e.Trim().Length == 0 || !SameEntry(e, entry)).ToList();
        if (kept.Count == entries.Length)
        {
            return null;
        }

        // Pemisah ganda akibat entri yang dibuang dirapikan; pemisah di ujung dipertahankan bila ada sebelumnya.
        string joined = string.Join(';', kept.Where(e => e.Trim().Length > 0));
        return value.EndsWith(';') && joined.Length > 0 ? joined + ";" : joined;
    }
}

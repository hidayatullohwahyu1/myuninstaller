using MyUninstaller.Core.Models;
using MyUninstaller.Core.Settings;

namespace MyUninstaller.Core.Listing;

/// <summary>Pencarian teks pada daftar program.</summary>
public static class ProgramSearch
{
    /// <summary>
    /// True bila semua kata di <paramref name="query"/> ada di nama atau penerbit (tidak peka huruf besar/kecil).
    /// Kueri kosong cocok dengan semua program.
    /// </summary>
    public static bool Matches(InstalledProgram program, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        foreach (string word in query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            bool found = program.DisplayName.Contains(word, StringComparison.CurrentCultureIgnoreCase)
                || (program.Publisher?.Contains(word, StringComparison.CurrentCultureIgnoreCase) ?? false);
            if (!found)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Urutan daftar program. Nilai kosong (ukuran/tanggal/penerbit tidak diketahui) selalu di akhir,
/// dan nilai yang sama diurutkan lagi berdasarkan nama.
/// </summary>
public sealed class ProgramComparer(ProgramSortKey key, bool descending) : IComparer<InstalledProgram>
{
    public ProgramSortKey Key { get; } = key;

    public bool Descending { get; } = descending;

    public int Compare(InstalledProgram? x, InstalledProgram? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return 1;
        }

        if (y is null)
        {
            return -1;
        }

        int result = Key switch
        {
            ProgramSortKey.Publisher => CompareNullsLast(Normalize(x.Publisher), Normalize(y.Publisher), CompareText),
            ProgramSortKey.Size => CompareNullsLast(x.SizeBytes, y.SizeBytes, (a, b) => a.CompareTo(b)),
            ProgramSortKey.InstallDate => CompareNullsLast(x.InstallDate, y.InstallDate, (a, b) => a.CompareTo(b)),
            _ => DirectionAware(CompareText(x.DisplayName, y.DisplayName)),
        };

        return result != 0 ? result : CompareText(x.DisplayName, y.DisplayName);
    }

    private static string? Normalize(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static int CompareText(string a, string b) => string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);

    private int DirectionAware(int result) => Descending ? -result : result;

    private int CompareNullsLast(string? a, string? b, Func<string, string, int> compare)
    {
        if (a is null && b is null)
        {
            return 0;
        }

        if (a is null)
        {
            return 1;
        }

        if (b is null)
        {
            return -1;
        }

        return DirectionAware(compare(a, b));
    }

    // Untuk struct nullable (long?, DateOnly?).
    private int CompareNullsLast<T>(T? a, T? b, Func<T, T, int> compare)
        where T : struct
    {
        if (a is null && b is null)
        {
            return 0;
        }

        if (a is null)
        {
            return 1;
        }

        if (b is null)
        {
            return -1;
        }

        return DirectionAware(compare(a.Value, b.Value));
    }
}

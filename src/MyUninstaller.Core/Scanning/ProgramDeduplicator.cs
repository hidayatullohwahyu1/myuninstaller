using MyUninstaller.Core.Models;

namespace MyUninstaller.Core.Scanning;

/// <summary>
/// Membuang entri ganda yang persis sama (nama, versi, penerbit, lokasi), misalnya program yang
/// mendaftar di HKLM dan HKCU sekaligus. Entri yang tampil diutamakan; lalu urutan sumber 64 → 32 → HKCU → Store.
/// </summary>
public static class ProgramDeduplicator
{
    public static IReadOnlyList<InstalledProgram> Deduplicate(IEnumerable<InstalledProgram> programs)
    {
        var result = new List<InstalledProgram>();
        var seen = new HashSet<(string, string, string, string)>();

        foreach (var program in programs
                     .OrderBy(p => p.IsSystemComponent)
                     .ThenBy(p => p.Source))
        {
            var key = (
                Normalize(program.DisplayName),
                Normalize(program.Version),
                Normalize(program.Publisher),
                Normalize(program.InstallLocation));

            if (seen.Add(key))
            {
                result.Add(program);
            }
        }

        return result;
    }

    private static string Normalize(string? value) => value?.Trim().ToUpperInvariant() ?? string.Empty;
}

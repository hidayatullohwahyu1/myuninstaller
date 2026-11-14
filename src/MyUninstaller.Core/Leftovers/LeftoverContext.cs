using MyUninstaller.Core.Models;
using MyUninstaller.Core.Scanning;

namespace MyUninstaller.Core.Leftovers;

/// <summary>
/// Konteks satu program yang sedang dicari sisanya, beserta program lain yang MASIH terpasang.
/// Semua aturan "jangan salah hapus milik program lain" ada di sini supaya bisa dites tanpa sistem berkas.
/// </summary>
public sealed class LeftoverContext
{
    private readonly List<string> _otherLocations;
    private readonly HashSet<string> _otherNames;

    public LeftoverContext(InstalledProgram program, IEnumerable<InstalledProgram> stillInstalled)
    {
        Program = program;
        Names = NameMatcher.ProgramNames(program);
        Publisher = NameMatcher.NormalizePublisher(program.Publisher);
        InstallLocation = NormalizeFolder(program.InstallLocation);

        var others = stillInstalled.Where(p => p.Id != program.Id).ToList();
        _otherLocations = [.. others.Select(p => NormalizeFolder(p.InstallLocation)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)];
        _otherNames = new HashSet<string>(others.SelectMany(NameMatcher.ProgramNames), StringComparer.Ordinal);

        // PRD: kunci/folder induk penerbit yang masih dipakai program lain dari penerbit sama tidak dihapus.
        PublisherShared = Publisher.Length > 0 && others.Any(p => NameMatcher.NormalizePublisher(p.Publisher) == Publisher);
    }

    public InstalledProgram Program { get; }

    /// <summary>Nama dasar program (sudah dinormalisasi).</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>Penerbit (dinormalisasi), kosong bila tidak ada atau terlalu umum.</summary>
    public string Publisher { get; }

    /// <summary>Folder instalasi (path lengkap tanpa garis miring akhir), bila layak.</summary>
    public string? InstallLocation { get; }

    /// <summary>Ada program lain dari penerbit yang sama yang masih terpasang.</summary>
    public bool PublisherShared { get; }

    /// <summary>Folder "penerbit" (nama penerbit) yang boleh ditelusuri ke dalam.</summary>
    public bool IsPublisherName(string? name) =>
        Publisher.Length > 0
        && NameMatcher.NormalizePublisher(name) is { Length: >= 4 } n
        && (n == Publisher || Publisher.StartsWith(n, StringComparison.Ordinal));

    /// <summary>
    /// Bukti berdasarkan nama untuk folder/kunci bernama <paramref name="name"/>, atau null bila bukan sisa
    /// program ini (termasuk bila nama itu juga milik program lain yang masih terpasang).
    /// </summary>
    /// <param name="name">Nama folder/kunci.</param>
    /// <param name="underPublisher">Kandidat berada di dalam folder/kunci bernama penerbit.</param>
    public LeftoverEvidence? EvidenceForName(string? name, bool underPublisher)
    {
        NameMatch match = NameMatcher.Compare(name, Names);
        string normalized = NameMatcher.Normalize(name);

        // Nama yang sama dengan program lain yang masih terpasang: ambigu, jangan disentuh.
        if (_otherNames.Contains(normalized))
        {
            return null;
        }

        return match switch
        {
            NameMatch.Exact => underPublisher ? LeftoverEvidence.ExactNameUnderPublisher : LeftoverEvidence.ExactName,
            NameMatch.Similar => LeftoverEvidence.SimilarName,
            _ when !underPublisher && !PublisherShared && IsPublisherName(name) => LeftoverEvidence.PublisherOnly,
            _ => null,
        };
    }

    /// <summary>
    /// True bila path (berkas/folder) milik program lain yang masih terpasang: berada di dalam folder instalasinya,
    /// atau justru membungkusnya (misalnya folder penerbit yang memuat program lain).
    /// </summary>
    public bool BelongsToOtherProgram(string path)
    {
        string? candidate = NormalizeFolder(path);
        if (candidate is null)
        {
            return true;
        }

        foreach (string other in _otherLocations)
        {
            if (IsSameOrInside(candidate, other) || IsSameOrInside(other, candidate))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True bila berkas berada di dalam folder instalasi program ini.</summary>
    public bool IsInsideInstallLocation(string? filePath)
    {
        if (InstallLocation is null || string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        string? normalized = NormalizeFolder(filePath);
        return normalized is not null && IsSameOrInside(normalized, InstallLocation) && !BelongsToOtherProgram(normalized);
    }

    /// <summary>Path dari baris perintah/nilai registri (mis. "\"C:\App\app.exe\" /bg") berada di folder program?</summary>
    public bool CommandPointsIntoInstallLocation(string? commandLine)
    {
        if (InstallLocation is null || string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        string text = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        string? exe = IconLocation.ExecutableFromCommandLine(text) ?? IconLocation.Parse(text)?.Path;
        if (exe is not null && Path.IsPathFullyQualified(exe) && IsInsideInstallLocation(exe))
        {
            return true;
        }

        // Cadangan: baris perintah memuat path folder (misalnya rundll32 "C:\App\x.dll",Entry).
        return text.Contains(InstallLocation + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               && !_otherLocations.Any(o => text.Contains(o + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                                             && o.Length > InstallLocation.Length);
    }

    private static bool IsSameOrInside(string path, string folder) =>
        path.Equals(folder, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            string trimmed = path.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(trimmed))
            {
                return null;
            }

            string full = Path.GetFullPath(trimmed);
            return full.Length > 3 ? full.TrimEnd('\\', '/') : null; // akar drive tidak pernah dipakai
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}

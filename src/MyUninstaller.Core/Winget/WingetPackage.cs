using System.Text.RegularExpressions;

namespace MyUninstaller.Core.Winget;

/// <summary>Satu baris <c>winget list</c>: paket terpasang yang dikenal sumber winget.</summary>
public sealed record WingetPackage(string Name, string Id, string Version, string? Available)
{
    public bool HasUpdate => !string.IsNullOrWhiteSpace(Available);
}

/// <summary>Hasil membaca daftar winget.</summary>
/// <param name="Packages">Paket yang terbaca (kosong bila gagal).</param>
/// <param name="Error">Null bila berhasil; selain itu pesan galat dari winget (baris terakhir yang bermakna).</param>
public sealed record WingetListResult(IReadOnlyList<WingetPackage> Packages, string? Error, bool WingetMissing = false);

public enum WingetAction
{
    Upgrade,
    UpgradeAll,
    Reinstall,
}

public interface IWingetClient
{
    Task<WingetListResult> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Menjalankan winget di jendela konsol yang terlihat (pengguna melihat kemajuan dan menyetujui sendiri
    /// ketentuan/lisensi), lalu menunggu jendela ditutup. Null bila winget tidak bisa dijalankan.
    /// </summary>
    Task<int?> RunInConsoleAsync(WingetAction action, string? packageId, CancellationToken cancellationToken);
}

/// <summary>Aturan murni untuk winget: parser tabel, validasi ID, argumen perintah.</summary>
public static partial class WingetRules
{
    /// <summary>ID winget: Penerbit.Nama (huruf, angka, titik, minus, garis bawah, plus). Tanpa spasi/kutip/meta cmd.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-_+]{0,127}$")]
    private static partial Regex IdRegex();

    public static bool IsValidId(string? id) => id is not null && IdRegex().IsMatch(id);

    /// <summary>Argumen winget (tanpa nama exe). ID harus sudah lolos <see cref="IsValidId"/>.</summary>
    public static string Arguments(WingetAction action, string? id)
    {
        if (action != WingetAction.UpgradeAll && !IsValidId(id))
        {
            throw new ArgumentException("ID paket winget tidak sah.", nameof(id));
        }

        return action switch
        {
            WingetAction.Upgrade => $"upgrade --id {id} --exact --source winget",
            WingetAction.Reinstall => $"install --id {id} --exact --source winget --force",
            _ => "upgrade --all --source winget",
        };
    }

    /// <summary>
    /// Membaca tabel keluaran <c>winget list</c>. Judul kolom bisa dilokalkan, jadi kolom ditentukan dari POSISI
    /// kata pada baris judul (baris tepat di atas garis "-----"): Nama, Id, Versi, [Tersedia], Sumber.
    /// Indikator kemajuan (\r, spinner) dibuang dulu.
    /// </summary>
    public static IReadOnlyList<WingetPackage> ParseList(string output)
    {
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(line => line[(line.LastIndexOf('\r') + 1)..].TrimEnd())
            .ToList();

        int dashes = lines.FindIndex(l => l.Length >= 10 && l.All(c => c == '-'));
        if (dashes < 1)
        {
            return [];
        }

        // Dengan --source winget kolomnya: Nama, Id, Versi, [Tersedia] (kolom Sumber tidak ada).
        string header = lines[dashes - 1];
        var starts = new List<int>();
        for (int i = 0; i < header.Length; i++)
        {
            if (header[i] != ' ' && (i == 0 || header[i - 1] == ' '))
            {
                starts.Add(i);
            }
        }

        // Judul kolom satu kata (juga versi bahasa Indonesia); minimal Nama, Id, Versi.
        if (starts.Count < 3 || starts.Count > 5)
        {
            return [];
        }

        bool hasAvailable = starts.Count >= 4;
        var result = new List<WingetPackage>();
        foreach (string line in lines.Skip(dashes + 1))
        {
            if (line.Length <= starts[2])
            {
                continue; // Baris ringkasan ("3 pembaruan tersedia.") atau kosong.
            }

            string Column(int index)
            {
                int start = starts[index];
                if (start >= line.Length)
                {
                    return string.Empty;
                }

                int end = index + 1 < starts.Count ? Math.Min(starts[index + 1], line.Length) : line.Length;
                return line[start..end].Trim();
            }

            string id = Column(1);
            string version = Column(2);
            if (!IsValidId(id) || version.Length == 0)
            {
                continue;
            }

            string? available = hasAvailable ? Column(3) : null;
            result.Add(new WingetPackage(Column(0).TrimEnd('…').Trim(), id, version, string.IsNullOrWhiteSpace(available) ? null : available));
        }

        return result;
    }

    /// <summary>Baris terakhir yang bermakna dari keluaran galat (untuk ditampilkan).</summary>
    public static string? LastMessage(string output) =>
        output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(l => l[(l.LastIndexOf('\r') + 1)..].Trim())
            .LastOrDefault(l => l.Length > 3 && l.Any(char.IsLetter) && !l.All(c => c is '-' or '█' or '▒' or ' '));
}

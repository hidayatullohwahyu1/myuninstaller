using System.Globalization;

namespace MyUninstaller.Core.Formatting;

/// <summary>
/// Format angka, ukuran, dan tanggal sesuai EYD: desimal koma (38,2 GB),
/// ribuan titik (1.250), tanggal "2 September 2026", jam "14.30".
/// </summary>
public static class IndonesianFormat
{
    private static readonly string[] MonthNames =
    [
        "Januari", "Februari", "Maret", "April", "Mei", "Juni",
        "Juli", "Agustus", "September", "Oktober", "November", "Desember",
    ];

    private static readonly string[] SizeUnits = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>Format angka eksplisit agar tidak bergantung pada data ICU sistem.</summary>
    public static NumberFormatInfo NumberFormat { get; } = new()
    {
        NumberDecimalSeparator = ",",
        NumberGroupSeparator = ".",
        NumberGroupSizes = [3],
        NegativeSign = "-",
    };

    /// <summary>Contoh: 1250 → "1.250".</summary>
    public static string Count(long value) => value.ToString("#,0", NumberFormat);

    /// <summary>
    /// Ukuran berkas dengan basis 1024. Di bawah 10 satuan memakai satu desimal (9,5 MB),
    /// di atasnya dibulatkan (412 MB); satu desimal untuk GB ke atas (38,2 GB).
    /// </summary>
    public static string Size(long bytes)
    {
        if (bytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), bytes, "Ukuran tidak boleh negatif.");
        }

        if (bytes < 1024)
        {
            return $"{bytes.ToString("0", NumberFormat)} B";
        }

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < SizeUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // Pembulatan bisa menghasilkan 1024 (misalnya 1023,96 MB); naikkan satuan.
        bool useDecimal = unit >= 3 || value < 10;
        double rounded = Math.Round(value, useDecimal ? 1 : 0, MidpointRounding.AwayFromZero);
        if (rounded >= 1024 && unit < SizeUnits.Length - 1)
        {
            rounded = Math.Round(rounded / 1024, 1, MidpointRounding.AwayFromZero);
            unit++;
            useDecimal = true;
        }

        string text = rounded.ToString(useDecimal ? "#,0.#" : "#,0", NumberFormat);
        return $"{text} {SizeUnits[unit]}";
    }

    /// <summary>Contoh: 2026-09-02 → "2 September 2026".</summary>
    public static string Date(DateOnly date) => $"{date.Day} {MonthNames[date.Month - 1]} {date.Year}";

    /// <summary>Contoh: 14:30 → "14.30".</summary>
    public static string Time(TimeOnly time) => $"{time.Hour:00}.{time.Minute:00}";

    /// <summary>Contoh: "2 September 2026 14.30".</summary>
    public static string DateTime(DateTime value) =>
        $"{Date(DateOnly.FromDateTime(value))} {Time(TimeOnly.FromDateTime(value))}";

    /// <summary>
    /// Membaca InstallDate registri (yyyyMMdd). Nilai rusak menghasilkan null.
    /// </summary>
    public static DateOnly? ParseRegistryDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateOnly.TryParseExact(value.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }
}

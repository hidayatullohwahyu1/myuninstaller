namespace MyUninstaller.Core.Settings;

/// <summary>Cara daftar program ditampilkan.</summary>
public enum ProgramViewMode
{
    /// <summary>Kisi ikon besar (bawaan, seperti Your Uninstaller!).</summary>
    Icons,

    /// <summary>Tabel berkolom.</summary>
    Details,
}

/// <summary>Kunci pengurutan daftar program.</summary>
public enum ProgramSortKey
{
    Name,
    Publisher,
    Size,
    InstallDate,
}

/// <summary>Posisi dan ukuran jendela terakhir (dalam satuan DIP).</summary>
public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool IsMaximized);

/// <summary>Pengaturan per pengguna yang disimpan di %LocalAppData%\MyUninstaller.</summary>
public sealed record AppSettings
{
    public WindowPlacement? MainWindow { get; init; }

    public ProgramViewMode ViewMode { get; init; } = ProgramViewMode.Icons;

    public ProgramSortKey SortKey { get; init; } = ProgramSortKey.Name;

    public bool SortDescending { get; init; }

    public bool ShowSystemComponents { get; init; }

    /// <summary>Mode terakhir di dialog hapus instalan (PRD: diingat, cukup tekan Enter).</summary>
    public Uninstall.UninstallMode LastUninstallMode { get; init; } = Uninstall.UninstallMode.Normal;

    /// <summary>Buat titik pemulihan sebelum sesi hapus instalan.</summary>
    public bool CreateRestorePoint { get; init; } = true;

    /// <summary>Lama karantina sebelum dikosongkan otomatis.</summary>
    public int QuarantineRetentionDays { get; init; } = 30;
}

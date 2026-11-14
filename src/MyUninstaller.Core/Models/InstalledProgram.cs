namespace MyUninstaller.Core.Models;

/// <summary>Asal entri program terpasang.</summary>
public enum ProgramSource
{
    /// <summary>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall (64-bit).</summary>
    Registry64,

    /// <summary>HKLM\SOFTWARE\WOW6432Node\...\Uninstall (32-bit).</summary>
    Registry32,

    /// <summary>HKCU\...\Uninstall (per pengguna).</summary>
    CurrentUser,

    /// <summary>Paket Microsoft Store/UWP.</summary>
    Store,
}

/// <summary>Satu program terpasang seperti yang tampil di daftar.</summary>
public sealed record InstalledProgram
{
    /// <summary>Batas hari untuk label "BARU".</summary>
    public const int NewThresholdDays = 7;

    /// <summary>Batas hari tidak dipakai untuk label "JARANG".</summary>
    public const int RarelyUsedThresholdDays = 90;

    /// <summary>Kunci unik: nama subkunci registri atau PackageFullName.</summary>
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required ProgramSource Source { get; init; }

    public string? Publisher { get; init; }

    public string? Version { get; init; }

    /// <summary>Ukuran dalam bita; null bila tidak diketahui.</summary>
    public long? SizeBytes { get; init; }

    public DateOnly? InstallDate { get; init; }

    /// <summary>Perkiraan terakhir dipakai (versi 1.2+); null bila tidak diketahui.</summary>
    public DateOnly? LastUsed { get; init; }

    public string? InstallLocation { get; init; }

    /// <summary>Path ikon (DisplayIcon), bisa berisi indeks seperti "app.exe,0".</summary>
    public string? IconPath { get; init; }

    public string? UninstallString { get; init; }

    public string? QuietUninstallString { get; init; }

    public string? HelpLink { get; init; }

    public string? UrlInfoAbout { get; init; }

    /// <summary>
    /// Disembunyikan bawaan: SystemComponent=1, pembaruan Windows, entri tanpa nama atau tanpa uninstaller,
    /// atau aplikasi sistem Store. Tampil bila opsi "Tampilkan komponen sistem" dinyalakan.
    /// </summary>
    public bool IsSystemComponent { get; init; }

    /// <summary>Path lengkap kunci registri, misalnya HKEY_LOCAL_MACHINE\SOFTWARE\...\Uninstall\7-Zip.</summary>
    public string? RegistryKeyPath { get; init; }

    /// <summary>Dipasang lewat Windows Installer (MSI).</summary>
    public bool IsWindowsInstaller { get; init; }

    /// <summary>Kode produk MSI bila nama kunci berupa GUID.</summary>
    public Guid? ProductCode { get; init; }

    /// <summary>True bila ukuran dihitung dari folder, bukan dari EstimatedSize.</summary>
    public bool IsSizeComputed { get; init; }

    /// <summary>Situs penerbit yang paling layak ditampilkan.</summary>
    public string? WebsiteUrl => !string.IsNullOrWhiteSpace(UrlInfoAbout) ? UrlInfoAbout : HelpLink;

    /// <summary>True bila dipasang dalam <see cref="NewThresholdDays"/> hari terakhir.</summary>
    public bool IsNew(DateOnly today) =>
        InstallDate is { } date && date <= today && today.DayNumber - date.DayNumber < NewThresholdDays;

    /// <summary>True bila diketahui tidak dipakai selama <see cref="RarelyUsedThresholdDays"/> hari atau lebih.</summary>
    public bool IsRarelyUsed(DateOnly today) =>
        LastUsed is { } date && today.DayNumber - date.DayNumber >= RarelyUsedThresholdDays;
}

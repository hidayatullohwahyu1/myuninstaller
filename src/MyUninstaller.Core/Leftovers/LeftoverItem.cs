using MyUninstaller.Core.Safety;

namespace MyUninstaller.Core.Leftovers;

/// <summary>Jenis sisa instalan.</summary>
public enum LeftoverKind
{
    Folder,
    File,
    Shortcut,
    RegistryKey,
    RegistryValue,
    Service,
    ScheduledTask,

    /// <summary>Entri PATH pengguna yang menunjuk ke folder program (Location = HKCU\Environment, ValueName = entri).</summary>
    PathEntry,
}

/// <summary>Bukti mengapa sesuatu dianggap sisa program (menentukan skor).</summary>
public enum LeftoverEvidence
{
    /// <summary>Folder InstallLocation program itu sendiri.</summary>
    InstallLocation,

    /// <summary>Kunci Uninstall program yang tertinggal.</summary>
    UninstallKey,

    /// <summary>Menunjuk ke berkas di dalam folder instalasi (pintasan, layanan, tugas, Run, CLSID, App Paths).</summary>
    PointsIntoInstallLocation,

    /// <summary>Nama sama persis dengan nama program/folder instalasi, di dalam folder/kunci penerbit.</summary>
    ExactNameUnderPublisher,

    /// <summary>Nama sama persis dengan nama program/folder instalasi.</summary>
    ExactName,

    /// <summary>Hanya nama penerbit (tidak ada program lain dari penerbit yang sama).</summary>
    PublisherOnly,

    /// <summary>Nama sama persis di lokasi yang bukan tempat data program biasa (misalnya pintasan "advertised").</summary>
    ExactNameElsewhere,

    /// <summary>
    /// Nama sama persis di folder data pengguna (Dokumen, profil) atau hasil pencarian seluruh drive.
    /// Bisa berisi karya pengguna, jadi selalu Ragu: tersembunyi dan butuh konfirmasi kedua.
    /// </summary>
    UserData,

    /// <summary>Nama mirip tetapi tidak persis.</summary>
    SimilarName,

    /// <summary>Tercatat dibuat oleh installer program ini saat pemasangannya dipantau (versi 2.0).</summary>
    RecordedAtInstall,
}

/// <summary>Kelompok tampilan (PRD "Skor keyakinan").</summary>
public enum LeftoverGroup
{
    /// <summary>80–100: dicentang bawaan.</summary>
    Safe,

    /// <summary>50–79: tidak dicentang.</summary>
    Maybe,

    /// <summary>Di bawah 50: disembunyikan, bisa dibuka.</summary>
    Doubtful,
}

/// <summary>Satu temuan sisa instalan.</summary>
public sealed record LeftoverItem
{
    public required LeftoverKind Kind { get; init; }

    /// <summary>Path berkas/folder, path kunci registri, nama layanan, atau path tugas.</summary>
    public required string Location { get; init; }

    /// <summary>Nama nilai registri (untuk <see cref="LeftoverKind.RegistryValue"/>).</summary>
    public string? ValueName { get; init; }

    public required LeftoverEvidence Evidence { get; init; }

    public required int Score { get; init; }

    /// <summary>Program asal temuan (untuk layar hasil gabungan uninstall massal).</summary>
    public required string ProgramName { get; init; }

    /// <summary>Keterangan tambahan, misalnya target pintasan atau path exe layanan.</summary>
    public string? Detail { get; init; }

    public long? SizeBytes { get; init; }

    public LeftoverGroup Group => ConfidenceScorer.GroupOf(Score);

    /// <summary>True bila berada di luar folder instalasi (butuh konfirmasi kedua bila skor rendah).</summary>
    public bool IsOutsideInstallLocation => Evidence is not (LeftoverEvidence.InstallLocation or LeftoverEvidence.PointsIntoInstallLocation);

    /// <summary>Target untuk <see cref="SafeRemover"/>.</summary>
    public RemovalTarget ToRemovalTarget() => Kind switch
    {
        LeftoverKind.Folder or LeftoverKind.File or LeftoverKind.Shortcut => new RemovalTarget(RemovalTargetKind.Path, Location),
        LeftoverKind.RegistryKey => new RemovalTarget(RemovalTargetKind.RegistryKey, Location),
        LeftoverKind.RegistryValue => new RemovalTarget(RemovalTargetKind.RegistryValue, Location, ValueName),
        LeftoverKind.Service => new RemovalTarget(RemovalTargetKind.Service, Location),
        LeftoverKind.PathEntry => new RemovalTarget(RemovalTargetKind.PathEntry, Location, ValueName),
        _ => new RemovalTarget(RemovalTargetKind.ScheduledTask, Location),
    };

    /// <summary>Kunci unik untuk membuang temuan ganda.</summary>
    public string Key => $"{Kind}|{Location}|{ValueName}".ToUpperInvariant();
}

namespace MyUninstaller.Core.Uninstall;

/// <summary>Mode hapus instalan (lihat PRD "Mode uninstall").</summary>
public enum UninstallMode
{
    /// <summary>Hanya menjalankan uninstaller program.</summary>
    Builtin,

    /// <summary>Bawaan + hapus otomatis sisa berlabel Aman.</summary>
    Safe,

    /// <summary>Bawaan + tampilkan sisa Aman dan Mungkin untuk dicentang (bawaan).</summary>
    Normal,

    /// <summary>Normal + pemindaian mendalam.</summary>
    Super,

    /// <summary>Lewati uninstaller bawaan, langsung hapus berkas dan registri (lewat karantina).</summary>
    Force,
}

public static class UninstallModeExtensions
{
    /// <summary>True bila mode menjalankan uninstaller bawaan program.</summary>
    public static bool RunsBuiltinUninstaller(this UninstallMode mode) => mode != UninstallMode.Force;

    /// <summary>True bila mode memindai sisa setelah uninstaller selesai.</summary>
    public static bool ScansLeftovers(this UninstallMode mode) => mode != UninstallMode.Builtin;
}

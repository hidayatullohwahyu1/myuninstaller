namespace MyUninstaller.Core.Uninstall;

/// <summary>Status akhir satu program (PRD: Berhasil / Sebagian / Gagal).</summary>
public enum UninstallOutcome
{
    /// <summary>Entri registri/paket hilang dan folder instalasi kosong atau sudah tidak ada.</summary>
    Success,

    /// <summary>Entri hilang, tetapi folder instalasi masih berisi berkas (sisa akan dipindai).</summary>
    Partial,

    /// <summary>Entri masih ada setelah uninstaller selesai.</summary>
    Failed,

    /// <summary>Dibatalkan pengguna (di uninstaller atau tombol Hentikan).</summary>
    Cancelled,

    /// <summary>Tidak ada uninstaller yang bisa dijalankan (entri rusak).</summary>
    NoUninstaller,
}

/// <summary>Hasil menjalankan proses penghapus instalan.</summary>
/// <param name="ExitCode">Kode keluar proses utama; null bila tidak diketahui (dihentikan/batas waktu).</param>
/// <param name="TimedOut">True bila melewati batas waktu.</param>
/// <param name="Cancelled">True bila dihentikan pengguna.</param>
/// <param name="Elapsed">Lama berjalan.</param>
public sealed record ProcessRunResult(int? ExitCode, bool TimedOut, bool Cancelled, TimeSpan Elapsed);

/// <summary>Hasil satu program.</summary>
public sealed record UninstallResult(
    UninstallOutcome Outcome,
    int? ExitCode,
    bool RestartRequired,
    TimeSpan Elapsed,
    string? ErrorMessage = null);

/// <summary>Menafsirkan kode keluar dan kondisi sesudah hapus instalan.</summary>
public static class UninstallEvaluator
{
    // Kode Windows Installer yang relevan.
    public const int MsiUserCancelled = 1602;
    public const int MsiAnotherInstallInProgress = 1618;
    public const int MsiUnknownProduct = 1605;
    public const int MsiRestartInitiated = 1641;
    public const int MsiRestartRequired = 3010;

    public static bool IsRestartRequired(int? exitCode) => exitCode is MsiRestartRequired or MsiRestartInitiated;

    /// <summary>Menentukan status akhir. Sumber kebenaran utama adalah hilangnya entri, bukan kode keluar.</summary>
    /// <param name="run">Hasil proses.</param>
    /// <param name="entryStillExists">Entri registri/paket masih ada.</param>
    /// <param name="installFolderHasFiles">Folder instalasi masih berisi berkas.</param>
    public static UninstallOutcome Evaluate(ProcessRunResult run, bool entryStillExists, bool installFolderHasFiles)
    {
        if (!entryStillExists)
        {
            return installFolderHasFiles ? UninstallOutcome.Partial : UninstallOutcome.Success;
        }

        if (run.Cancelled || run.ExitCode == MsiUserCancelled)
        {
            return UninstallOutcome.Cancelled;
        }

        return UninstallOutcome.Failed;
    }
}

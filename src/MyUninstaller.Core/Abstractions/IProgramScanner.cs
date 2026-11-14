using MyUninstaller.Core.Models;

namespace MyUninstaller.Core.Abstractions;

/// <summary>Mengambil daftar program terpasang (registri Uninstall dan paket Store).</summary>
public interface IProgramScanner
{
    /// <summary>
    /// Memindai program terpasang. Harus aman dipanggil dari utas latar belakang;
    /// entri yang rusak dilewati, bukan melempar galat.
    /// </summary>
    /// <param name="partialResults">
    /// Opsional: menerima hasil sementara (misalnya program Win32 sebelum paket Store selesai dibaca)
    /// agar daftar bisa tampil lebih cepat.
    /// </param>
    Task<IReadOnlyList<InstalledProgram>> ScanAsync(
        IProgress<IReadOnlyList<InstalledProgram>>? partialResults = null,
        CancellationToken cancellationToken = default);
}

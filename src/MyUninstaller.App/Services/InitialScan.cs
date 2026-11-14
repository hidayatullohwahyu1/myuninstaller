using MyUninstaller.Core.Abstractions;
using MyUninstaller.Core.Models;

namespace MyUninstaller.App.Services;

/// <summary>
/// Pemindaian pertama dimulai sedini mungkin (sejajar dengan pembuatan jendela utama), supaya daftar program
/// tampil lebih cepat (PRD: kurang dari 2 detik). Hasilnya dipakai sekali oleh pemuatan pertama.
/// </summary>
public sealed class InitialScan(IProgramScanner scanner)
{
    private Task<IReadOnlyList<InstalledProgram>>? _task;

    public void Start() => _task ??= Task.Run(() => scanner.ScanAsync());

    /// <summary>Mengambil pemindaian yang sudah berjalan (hanya sekali); null bila tidak ada.</summary>
    public Task<IReadOnlyList<InstalledProgram>>? Take()
    {
        var task = _task;
        _task = null;
        return task;
    }
}

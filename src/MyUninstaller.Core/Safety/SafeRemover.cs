namespace MyUninstaller.Core.Safety;

/// <summary>Jenis target penghapusan.</summary>
public enum RemovalTargetKind
{
    /// <summary>Berkas atau folder.</summary>
    Path,

    RegistryKey,

    /// <summary>Satu nilai di kunci registri (misalnya entri Run). <see cref="RemovalTarget.ValueName"/> wajib.</summary>
    RegistryValue,

    /// <summary>Layanan Windows pihak ketiga. Location = nama layanan.</summary>
    Service,

    /// <summary>Tugas terjadwal. Location = path tugas, misalnya \Vendor\Updater.</summary>
    ScheduledTask,
}

/// <summary>Satu hal yang akan dihapus (lewat karantina/cadangan).</summary>
public sealed record RemovalTarget(RemovalTargetKind Kind, string Location, string? ValueName = null);

public enum RemovalStatus
{
    Removed,

    /// <summary>Disalin ke karantina; aslinya dihapus saat mulai ulang.</summary>
    PendingReboot,

    /// <summary>Dilindungi daftar lindung.</summary>
    Protected,

    /// <summary>Sudah tidak ada.</summary>
    Missing,
    Failed,
}

public sealed record RemovalResult(RemovalTarget Target, RemovalStatus Status, long Bytes, string? Detail = null);

/// <summary>Layanan Windows: cadangan kunci layanan (.reg), hentikan, lalu hapus.</summary>
public interface IServiceRemover
{
    /// <summary>Informasi layanan, atau null bila tidak ada.</summary>
    ServiceInfo? Get(string serviceName);

    /// <summary>Hentikan lalu hapus layanan. Cadangan dibuat pemanggil sebelumnya.</summary>
    bool StopAndDelete(string serviceName);
}

/// <param name="Name">Nama layanan.</param>
/// <param name="ImagePath">Path exe/driver (sudah dibersihkan dari argumen dan kutip), bila ada.</param>
/// <param name="Type">Nilai Type registri (1 = driver kernel, 2 = driver sistem berkas, 0x10/0x20 = layanan).</param>
/// <param name="RegistryKeyPath">HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\&lt;nama&gt;.</param>
public sealed record ServiceInfo(string Name, string? ImagePath, int Type, string RegistryKeyPath);

/// <summary>Tugas terjadwal: ekspor XML lalu hapus; pulihkan dengan mengimpor XML.</summary>
public interface IScheduledTaskRemover
{
    bool Exists(string taskPath);

    /// <summary>Simpan definisi tugas (XML) ke folder sesi. Mengembalikan path berkas atau null.</summary>
    string? Export(string sessionId, string taskPath);

    bool Delete(string taskPath);

    bool Import(string xmlFile, string taskPath);
}

/// <summary>
/// Penghapus aman: SATU-SATUNYA jalur untuk menghapus berkas, registri, layanan, atau tugas terjadwal.
/// Urutan setiap target: cek daftar lindung (tepat sebelum hapus) → catat Pending di jurnal →
/// karantina / cadangan → hapus → perbarui jurnal. Tidak pernah menghapus permanen.
/// </summary>
public sealed class SafeRemover(
    ProtectedPaths protectedPaths,
    IQuarantineService quarantine,
    IRegistryBackupService registry,
    ISessionJournal journal,
    IServiceRemover? services = null,
    IScheduledTaskRemover? tasks = null)
{
    public IReadOnlyList<RemovalResult> Remove(
        string sessionId,
        IEnumerable<RemovalTarget> targets,
        IProgress<RemovalResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<RemovalResult>();

        // Layanan dan tugas dulu (mereka bisa mengunci berkas), lalu berkas, terakhir registri.
        foreach (var target in targets.OrderBy(t => t.Kind switch
                 {
                     RemovalTargetKind.Service => 0,
                     RemovalTargetKind.ScheduledTask => 1,
                     RemovalTargetKind.Path => 2,
                     _ => 3,
                 }))
        {
            cancellationToken.ThrowIfCancellationRequested();

            RemovalResult result;
            try
            {
                result = target.Kind switch
                {
                    RemovalTargetKind.Path => RemovePath(sessionId, target, cancellationToken),
                    RemovalTargetKind.RegistryKey => RemoveRegistryKey(sessionId, target),
                    RemovalTargetKind.RegistryValue => RemoveRegistryValue(sessionId, target),
                    RemovalTargetKind.Service => RemoveService(sessionId, target),
                    _ => RemoveTask(sessionId, target),
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Satu target gagal tidak menghentikan yang lain.
                result = new RemovalResult(target, RemovalStatus.Failed, 0, ex.Message);
            }

            results.Add(result);
            progress?.Report(result);
        }

        return results;
    }

    private RemovalResult RemovePath(string sessionId, RemovalTarget target, CancellationToken cancellationToken)
    {
        if (protectedPaths.IsProtected(target.Location))
        {
            return new RemovalResult(target, RemovalStatus.Protected, 0);
        }

        if (!File.Exists(target.Location) && !Directory.Exists(target.Location))
        {
            return new RemovalResult(target, RemovalStatus.Missing, 0);
        }

        long actionId = journal.LogPending(sessionId, JournalActionKind.QuarantinePath, target.Location);
        QuarantineResult moved = quarantine.Quarantine(sessionId, target.Location, cancellationToken);

        var (journalStatus, status) = moved.Status switch
        {
            QuarantineStatus.Moved => (JournalActionStatus.Done, RemovalStatus.Removed),
            QuarantineStatus.PendingReboot => (JournalActionStatus.Done, RemovalStatus.PendingReboot),
            QuarantineStatus.Skipped => (JournalActionStatus.Skipped, RemovalStatus.Failed),
            _ => (JournalActionStatus.Failed, RemovalStatus.Failed),
        };
        journal.Complete(actionId, journalStatus, moved.Detail, moved.QuarantinePath);

        return new RemovalResult(target, status, moved.Bytes, moved.Detail);
    }

    private RemovalResult RemoveRegistryKey(string sessionId, RemovalTarget target)
    {
        if (ProtectedRegistryKeys.IsProtected(target.Location))
        {
            return new RemovalResult(target, RemovalStatus.Protected, 0);
        }

        if (!registry.KeyExists(target.Location))
        {
            return new RemovalResult(target, RemovalStatus.Missing, 0);
        }

        long actionId = journal.LogPending(sessionId, JournalActionKind.DeleteRegistryKey, target.Location);

        // Cadangan .reg wajib ada sebelum menghapus.
        string? backup = registry.Export(sessionId, target.Location);
        if (backup is null)
        {
            journal.Complete(actionId, JournalActionStatus.Failed, "Ekspor .reg gagal; kunci tidak dihapus.");
            return new RemovalResult(target, RemovalStatus.Failed, 0, "Ekspor .reg gagal");
        }

        bool deleted = registry.DeleteKey(target.Location);
        journal.Complete(actionId, deleted ? JournalActionStatus.Done : JournalActionStatus.Failed, null, backup);
        return new RemovalResult(target, deleted ? RemovalStatus.Removed : RemovalStatus.Failed, 0);
    }

    private RemovalResult RemoveRegistryValue(string sessionId, RemovalTarget target)
    {
        if (target.ValueName is null || ProtectedRegistryKeys.IsProtectedValue(target.Location, target.ValueName))
        {
            return new RemovalResult(target, RemovalStatus.Protected, 0);
        }

        if (!registry.ValueExists(target.Location, target.ValueName))
        {
            return new RemovalResult(target, RemovalStatus.Missing, 0);
        }

        long actionId = journal.LogPending(sessionId, JournalActionKind.DeleteRegistryValue, $@"{target.Location}\{target.ValueName}");

        // Cadangan kunci induk: impor ulang mengembalikan nilai yang dihapus (nilai lain tidak berubah).
        string? backup = registry.Export(sessionId, target.Location);
        if (backup is null)
        {
            journal.Complete(actionId, JournalActionStatus.Failed, "Ekspor .reg gagal; nilai tidak dihapus.");
            return new RemovalResult(target, RemovalStatus.Failed, 0, "Ekspor .reg gagal");
        }

        bool deleted = registry.DeleteValue(target.Location, target.ValueName);
        journal.Complete(actionId, deleted ? JournalActionStatus.Done : JournalActionStatus.Failed, null, backup);
        return new RemovalResult(target, deleted ? RemovalStatus.Removed : RemovalStatus.Failed, 0);
    }

    private RemovalResult RemoveService(string sessionId, RemovalTarget target)
    {
        if (services is null)
        {
            return new RemovalResult(target, RemovalStatus.Failed, 0, "Penghapus layanan tidak tersedia");
        }

        ServiceInfo? info = services.Get(target.Location);
        if (info is null)
        {
            return new RemovalResult(target, RemovalStatus.Missing, 0);
        }

        if (ProtectedServices.IsProtected(info))
        {
            return new RemovalResult(target, RemovalStatus.Protected, 0);
        }

        long actionId = journal.LogPending(sessionId, JournalActionKind.DeleteService, info.Name);
        string? backup = registry.Export(sessionId, info.RegistryKeyPath);
        if (backup is null)
        {
            journal.Complete(actionId, JournalActionStatus.Failed, "Ekspor .reg layanan gagal; layanan tidak dihapus.");
            return new RemovalResult(target, RemovalStatus.Failed, 0, "Ekspor .reg gagal");
        }

        bool deleted = services.StopAndDelete(info.Name);
        journal.Complete(actionId, deleted ? JournalActionStatus.Done : JournalActionStatus.Failed, null, backup);
        return new RemovalResult(target, deleted ? RemovalStatus.Removed : RemovalStatus.Failed, 0);
    }

    private RemovalResult RemoveTask(string sessionId, RemovalTarget target)
    {
        if (tasks is null)
        {
            return new RemovalResult(target, RemovalStatus.Failed, 0, "Penghapus tugas tidak tersedia");
        }

        if (ProtectedServices.IsProtectedTask(target.Location))
        {
            return new RemovalResult(target, RemovalStatus.Protected, 0);
        }

        if (!tasks.Exists(target.Location))
        {
            return new RemovalResult(target, RemovalStatus.Missing, 0);
        }

        long actionId = journal.LogPending(sessionId, JournalActionKind.DeleteScheduledTask, target.Location);
        string? backup = tasks.Export(sessionId, target.Location);
        if (backup is null)
        {
            journal.Complete(actionId, JournalActionStatus.Failed, "Ekspor XML tugas gagal; tugas tidak dihapus.");
            return new RemovalResult(target, RemovalStatus.Failed, 0, "Ekspor XML gagal");
        }

        bool deleted = tasks.Delete(target.Location);
        journal.Complete(actionId, deleted ? JournalActionStatus.Done : JournalActionStatus.Failed, null, backup);
        return new RemovalResult(target, deleted ? RemovalStatus.Removed : RemovalStatus.Failed, 0);
    }
}

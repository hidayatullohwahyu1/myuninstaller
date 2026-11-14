namespace MyUninstaller.Core.Safety;

/// <summary>Jenis aksi yang dicatat di jurnal sesi.</summary>
public enum JournalActionKind
{
    RestorePoint,
    RunUninstaller,
    KillProcess,

    /// <summary>Berkas/folder dipindah ke karantina. BackupPath = lokasi di karantina.</summary>
    QuarantinePath,

    /// <summary>Kunci registri diekspor (.reg) lalu dihapus. BackupPath = berkas .reg.</summary>
    DeleteRegistryKey,

    /// <summary>Satu nilai registri dihapus. BackupPath = .reg kunci induknya.</summary>
    DeleteRegistryValue,

    /// <summary>Layanan dihentikan dan dihapus. Target = nama layanan, BackupPath = .reg kunci layanan.</summary>
    DeleteService,

    /// <summary>Tugas terjadwal dihapus. Target = path tugas, BackupPath = berkas XML.</summary>
    DeleteScheduledTask,
}

/// <summary>Status aksi di jurnal.</summary>
public enum JournalActionStatus
{
    /// <summary>Dicatat, belum dijalankan (bila aplikasi crash di sini, aksi mungkin setengah jalan).</summary>
    Pending,
    Done,
    Failed,
    Skipped,

    /// <summary>Sudah dipulihkan (Batalkan).</summary>
    Restored,
}

/// <summary>Status sesi.</summary>
public enum JournalSessionStatus
{
    Running,
    Completed,

    /// <summary>Aplikasi tertutup/crash sebelum sesi selesai.</summary>
    Interrupted,
    Restored,
}

public sealed record JournalSession(
    string Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string Mode,
    string Title,
    JournalSessionStatus Status,
    string? Note);

public sealed record JournalAction(
    long Id,
    string SessionId,
    JournalActionKind Kind,
    string Target,
    string? BackupPath,
    JournalActionStatus Status,
    string? Detail,
    DateTimeOffset CreatedAt);

/// <summary>
/// Jurnal sesi dan riwayat (SQLite). Aturan: setiap aksi dicatat <see cref="JournalActionStatus.Pending"/>
/// SEBELUM dijalankan, lalu diperbarui sesudahnya.
/// </summary>
public interface ISessionJournal
{
    string StartSession(string mode, string title);

    void FinishSession(string sessionId, JournalSessionStatus status, string? note = null);

    long LogPending(string sessionId, JournalActionKind kind, string target, string? backupPath = null);

    void Complete(long actionId, JournalActionStatus status, string? detail = null, string? backupPath = null);

    IReadOnlyList<JournalSession> GetSessions(int limit = 200);

    IReadOnlyList<JournalAction> GetActions(string sessionId);

    /// <summary>Sesi yang belum selesai (aplikasi crash/listrik padam).</summary>
    IReadOnlyList<JournalSession> GetUnfinishedSessions();
}

/// <summary>Hasil memindahkan ke karantina.</summary>
public enum QuarantineStatus
{
    Moved,

    /// <summary>Disalin ke karantina; aslinya sedang dipakai dan dijadwalkan dihapus saat mulai ulang.</summary>
    PendingReboot,

    /// <summary>Tidak dipindah (dilindungi, tautan simbolik, tidak ada, atau akses ditolak).</summary>
    Skipped,
    Failed,
}

public sealed record QuarantineResult(QuarantineStatus Status, string? QuarantinePath, long Bytes, string? Detail = null);

/// <summary>Karantina berkas/folder: dipindah, bukan dihapus permanen.</summary>
public interface IQuarantineService
{
    QuarantineResult Quarantine(string sessionId, string path, CancellationToken cancellationToken = default);

    /// <summary>Mengembalikan dari karantina ke lokasi asal. False bila lokasi asal sudah terisi.</summary>
    bool Restore(string quarantinePath, string originalPath);

    /// <summary>Mengosongkan folder sesi karantina yang lebih tua dari batas hari. Mengembalikan jumlah folder sesi yang dihapus.</summary>
    int PurgeExpired(int retentionDays);

    /// <summary>
    /// Menghapus PERMANEN isi karantina satu sesi (atas permintaan pengguna, dengan konfirmasi).
    /// Setelah ini sesi tidak bisa dibatalkan lagi.
    /// </summary>
    bool PurgeSession(string sessionId);

    /// <summary>Folder karantina satu sesi (berkas dan cadangan .reg/.xml).</summary>
    string SessionFolder(string sessionId);
}

/// <summary>Cadangan dan penghapusan kunci registri.</summary>
public interface IRegistryBackupService
{
    bool KeyExists(string keyPath);

    /// <summary>Ekspor kunci ke berkas .reg di folder sesi. Mengembalikan path berkas, atau null bila gagal.</summary>
    string? Export(string sessionId, string keyPath);

    /// <summary>Menghapus kunci beserta subkuncinya. Hanya dipanggil setelah ekspor berhasil.</summary>
    bool DeleteKey(string keyPath);

    /// <summary>Mengimpor ulang berkas .reg (Batalkan).</summary>
    bool Import(string regFile);

    bool ValueExists(string keyPath, string valueName);

    /// <summary>Menghapus satu nilai. Hanya dipanggil setelah kunci induk diekspor.</summary>
    bool DeleteValue(string keyPath, string valueName);
}

public enum RestorePointStatus
{
    Created,

    /// <summary>Windows menolak (biasanya batas satu titik pemulihan per 24 jam).</summary>
    Throttled,

    /// <summary>System Restore nonaktif.</summary>
    Disabled,
    Failed,
}

/// <summary>Titik pemulihan Windows (WMI SystemRestore).</summary>
public interface IRestorePointService
{
    Task<RestorePointStatus> CreateAsync(string description, CancellationToken cancellationToken);
}

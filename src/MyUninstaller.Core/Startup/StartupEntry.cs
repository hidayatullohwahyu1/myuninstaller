namespace MyUninstaller.Core.Startup;

/// <summary>Asal sebuah program awal.</summary>
public enum StartupSource
{
    /// <summary>HKLM\...\Run (64-bit), untuk semua pengguna.</summary>
    RunMachine,

    /// <summary>HKLM\SOFTWARE\WOW6432Node\...\Run (32-bit), untuk semua pengguna.</summary>
    RunMachine32,

    /// <summary>HKCU\...\Run, khusus pengguna ini.</summary>
    RunUser,

    /// <summary>Folder Startup pengguna ini.</summary>
    StartupFolderUser,

    /// <summary>Folder Startup semua pengguna.</summary>
    StartupFolderCommon,

    /// <summary>Tugas terjadwal dengan pemicu saat masuk/saat mulai (bukan milik \Microsoft\).</summary>
    ScheduledTask,
}

/// <summary>Satu program yang berjalan otomatis saat Windows dimulai atau pengguna masuk.</summary>
public sealed record StartupEntry
{
    /// <summary>Nama tampilan (nama nilai Run, nama berkas pintasan, atau nama tugas).</summary>
    public required string Name { get; init; }

    /// <summary>Baris perintah yang dijalankan.</summary>
    public required string Command { get; init; }

    public required StartupSource Source { get; init; }

    /// <summary>Kunci registri Run, path berkas di folder Startup, atau path tugas (\Vendor\Task).</summary>
    public required string Location { get; init; }

    /// <summary>Nama nilai di kunci StartupApproved (nama nilai Run atau nama berkas pintasan); null untuk tugas.</summary>
    public string? ApprovalName { get; init; }

    public required bool IsEnabled { get; init; }

    /// <summary>Exe yang dijalankan, bila bisa ditentukan.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>Kunci unik.</summary>
    public string Id => $"{Source}|{Location}|{ApprovalName}";
}

/// <summary>Membaca dan mengaktifkan/menonaktifkan program awal. Tidak pernah menghapus entri.</summary>
public interface IStartupRepository
{
    IReadOnlyList<StartupEntry> GetAll();

    /// <summary>Mengaktifkan/menonaktifkan dengan cara yang sama seperti Task Manager (bisa dibalik kapan saja).</summary>
    bool SetEnabled(StartupEntry entry, bool enabled);
}

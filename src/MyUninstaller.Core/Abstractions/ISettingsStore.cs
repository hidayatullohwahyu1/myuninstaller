using MyUninstaller.Core.Settings;

namespace MyUninstaller.Core.Abstractions;

/// <summary>Membaca dan menyimpan <see cref="AppSettings"/>.</summary>
public interface ISettingsStore
{
    /// <summary>Memuat pengaturan; berkas hilang atau rusak menghasilkan nilai bawaan.</summary>
    AppSettings Load();

    /// <summary>Menyimpan pengaturan secara atomik (tulis berkas sementara lalu ganti).</summary>
    void Save(AppSettings settings);
}

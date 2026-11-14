using System.Text.Json;
using System.Text.Json.Serialization;
using MyUninstaller.Core.Abstractions;
using MyUninstaller.Core.Settings;

namespace MyUninstaller.Infrastructure.Settings;

/// <summary>Menyimpan pengaturan sebagai JSON. Tidak pernah melempar galat ke pemanggil saat memuat.</summary>
public sealed class JsonSettingsStore(string filePath) : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lock _gate = new();

    public JsonSettingsStore(AppPaths paths)
        : this(paths.SettingsFile)
    {
    }

    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    return new AppSettings();
                }

                string json = File.ReadAllText(filePath);
                return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                // Berkas rusak tidak boleh membuat aplikasi gagal dibuka.
                return new AppSettings();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate)
        {
            string directory = Path.GetDirectoryName(filePath)!;
            Directory.CreateDirectory(directory);

            string tempFile = filePath + ".tmp";
            File.WriteAllText(tempFile, JsonSerializer.Serialize(settings, Options));
            File.Move(tempFile, filePath, overwrite: true);
        }
    }
}

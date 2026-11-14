using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MyUninstaller.Core.Monitoring;

namespace MyUninstaller.Infrastructure.Monitoring;

/// <summary>Jejak instalasi sebagai berkas JSON, satu per program, di %ProgramData%\MyUninstaller\Jejak.</summary>
public sealed partial class JsonInstallTraceStore(string folder, ILogger<JsonInstallTraceStore> logger) : IInstallTraceStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // ID dibuat aplikasi (GUID "N"); divalidasi agar nama berkas tidak bisa keluar dari folder jejak.
    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex IdRegex();

    public IReadOnlyList<InstallTrace> GetAll()
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var traces = new List<InstallTrace>();
        foreach (string file in Directory.EnumerateFiles(folder, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<InstallTrace>(File.ReadAllText(file), Json) is { } trace && IdRegex().IsMatch(trace.Id))
                {
                    traces.Add(trace);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Satu berkas rusak tidak menghentikan yang lain.
                logger.LogWarning(ex, "Jejak {File} tidak bisa dibaca", file);
            }
        }

        return [.. traces.OrderByDescending(t => t.CreatedAt)];
    }

    public void Save(InstallTrace trace)
    {
        string path = PathOf(trace.Id);
        Directory.CreateDirectory(folder);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(trace, Json));
        File.Move(temp, path, overwrite: true);
        logger.LogInformation("Jejak instalasi {Name} disimpan ({Count} benda)", trace.Name, trace.Items.Count);
    }

    public void Delete(string id)
    {
        string path = PathOf(id);
        if (File.Exists(path))
        {
            File.Delete(path);
            logger.LogInformation("Jejak instalasi {Id} dihapus", id);
        }
    }

    private string PathOf(string id) =>
        IdRegex().IsMatch(id) ? Path.Combine(folder, id + ".json") : throw new ArgumentException("ID jejak tidak sah.", nameof(id));
}

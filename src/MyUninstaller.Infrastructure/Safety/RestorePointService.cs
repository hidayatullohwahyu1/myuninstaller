using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MyUninstaller.Core.Safety;

namespace MyUninstaller.Infrastructure.Safety;

/// <summary>
/// Membuat titik pemulihan Windows lewat WMI root\default:SystemRestore. Windows biasanya hanya membuat
/// satu titik pemulihan per 24 jam; bila ditolak, jumlah titik tidak bertambah dan hasilnya Throttled.
/// Aplikasi TIDAK mengubah pengaturan System Restore (mis. SystemRestorePointCreationFrequency).
/// </summary>
public sealed class RestorePointService(ILogger<RestorePointService> logger) : IRestorePointService
{
    private const uint ApplicationUninstall = 1;
    private const uint BeginSystemChange = 100;
    private const uint ErrorServiceDisabled = 1058;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    public async Task<RestorePointStatus> CreateAsync(string description, CancellationToken cancellationToken)
    {
        // Batas deskripsi System Restore: 256 karakter.
        string text = description.Length > 250 ? description[..250] : description;

        try
        {
            return await Task.Run(() => Create(text), cancellationToken).WaitAsync(Timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Pembuatan titik pemulihan melewati batas waktu");
            return RestorePointStatus.Failed;
        }
    }

    private RestorePointStatus Create(string description)
    {
        try
        {
            int before = CountRestorePoints();

            using var restore = new ManagementClass(@"\\.\root\default", "SystemRestore", null);
            using var input = restore.GetMethodParameters("CreateRestorePoint");
            input["Description"] = description;
            input["RestorePointType"] = ApplicationUninstall;
            input["EventType"] = BeginSystemChange;

            using var output = restore.InvokeMethod("CreateRestorePoint", input, null);
            uint code = Convert.ToUInt32(output?["ReturnValue"] ?? 1u, System.Globalization.CultureInfo.InvariantCulture);

            if (code == ErrorServiceDisabled || code == 0x80070422)
            {
                logger.LogWarning("System Restore nonaktif (kode {Code})", code);
                return RestorePointStatus.Disabled;
            }

            if (code != 0)
            {
                logger.LogWarning("CreateRestorePoint gagal, kode {Code}", code);
                return RestorePointStatus.Failed;
            }

            int after = CountRestorePoints();
            if (after <= before)
            {
                logger.LogInformation("Titik pemulihan tidak dibuat Windows (batas frekuensi)");
                return RestorePointStatus.Throttled;
            }

            logger.LogInformation("Titik pemulihan dibuat: {Description}", description);
            return RestorePointStatus.Created;
        }
        catch (ManagementException ex)
        {
            logger.LogWarning(ex, "WMI SystemRestore gagal");
            return ex.ErrorCode == ManagementStatus.InvalidClass ? RestorePointStatus.Disabled : RestorePointStatus.Failed;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Titik pemulihan tidak bisa dibuat");
            return RestorePointStatus.Failed;
        }
    }

    private static int CountRestorePoints()
    {
        using var searcher = new ManagementObjectSearcher(@"\\.\root\default", "SELECT SequenceNumber FROM SystemRestore");
        using var results = searcher.Get();
        return results.Count;
    }
}

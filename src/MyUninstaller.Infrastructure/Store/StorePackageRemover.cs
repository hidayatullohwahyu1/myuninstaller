using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MyUninstaller.Core.Uninstall;
using Windows.Management.Deployment;

namespace MyUninstaller.Infrastructure.Store;

/// <summary>Menghapus paket Store/UWP pengguna saat ini lewat PackageManager.RemovePackageAsync.</summary>
public sealed class StorePackageRemover(ILogger<StorePackageRemover> logger) : IStorePackageRemover
{
    public async Task<ProcessRunResult> RemoveAsync(string packageFullName, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation("Menghapus paket Store {Package}", packageFullName);

        try
        {
            var manager = new PackageManager();
            DeploymentResult result = await manager.RemovePackageAsync(packageFullName).AsTask(cancellationToken).ConfigureAwait(false);
            int code = result.ExtendedErrorCode?.HResult ?? 0;
            if (code != 0)
            {
                logger.LogWarning("Penghapusan paket gagal ({Code:X8}): {Error}", code, result.ErrorText);
            }

            return new ProcessRunResult(code, false, false, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            return new ProcessRunResult(null, false, true, stopwatch.Elapsed);
        }
        catch (COMException ex)
        {
            logger.LogWarning(ex, "Penghapusan paket {Package} gagal", packageFullName);
            return new ProcessRunResult(ex.HResult, false, false, stopwatch.Elapsed);
        }
    }
}

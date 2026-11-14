using System.Diagnostics;
using System.Security;
using System.ServiceProcess;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using MyUninstaller.Core.Safety;
using MyUninstaller.Core.Scanning;

namespace MyUninstaller.Infrastructure.Safety;

/// <summary>Layanan Windows pihak ketiga: baca info dari registri, hentikan, lalu hapus dengan sc.exe.</summary>
public sealed class ServiceRemover(ILogger<ServiceRemover> logger) : IServiceRemover
{
    public const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);

    public ServiceInfo? Get(string serviceName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey($@"{ServicesKey}\{serviceName}", writable: false);
            return key is null ? null : Read(serviceName, key);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    internal static ServiceInfo Read(string name, RegistryKey key)
    {
        int type = key.GetValue("Type") is int t ? t : 0;
        string? raw = key.GetValue("ImagePath") as string;
        return new ServiceInfo(name, CleanImagePath(raw), type, $@"HKEY_LOCAL_MACHINE\{ServicesKey}\{name}");
    }

    /// <summary>"\"C:\App\svc.exe\" -k" atau "\??\C:\...\drv.sys" → path berkas saja.</summary>
    internal static string? CleanImagePath(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return null;
        }

        string text = Environment.ExpandEnvironmentVariables(imagePath.Trim());
        if (text.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            text = text[4..];
        }

        if (text.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), text[12..]);
        }

        return IconLocation.ExecutableFromCommandLine(text) ?? (text.StartsWith('"') ? text.Trim('"') : text.Split(' ')[0]);
    }

    public bool StopAndDelete(string serviceName)
    {
        try
        {
            using (var controller = new ServiceController(serviceName))
            {
                if (controller.Status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending) && controller.CanStop)
                {
                    controller.Stop();
                }

                controller.WaitForStatus(ServiceControllerStatus.Stopped, StopTimeout);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.ServiceProcess.TimeoutException)
        {
            // Tetap lanjut menghapus: layanan yang belum berhenti dihapus Windows saat mulai ulang.
            logger.LogWarning(ex, "Layanan {Service} tidak berhenti dengan bersih", serviceName);
        }

        string sc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(sc, $"delete \"{serviceName}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            })!;
            process.StandardOutput.ReadToEnd();
            bool exited = process.WaitForExit(30_000);
            bool ok = exited && process.ExitCode == 0;
            logger.LogInformation("Layanan {Service} dihapus: {Ok}", serviceName, ok);
            return ok;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "sc.exe delete gagal untuk {Service}", serviceName);
            return false;
        }
    }
}

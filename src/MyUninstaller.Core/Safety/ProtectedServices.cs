namespace MyUninstaller.Core.Safety;

/// <summary>
/// Daftar lindung layanan dan tugas terjadwal (PRD): layanan/driver bawaan Microsoft tidak pernah disentuh;
/// driver kernel tidak ditawarkan sama sekali (butuh mulai ulang dan berisiko gagal boot).
/// </summary>
public static class ProtectedServices
{
    private const int KernelDriver = 0x1;
    private const int FileSystemDriver = 0x2;
    private const int Adapter = 0x4;
    private const int RecognizerDriver = 0x8;

    private static readonly HashSet<string> CriticalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "RpcSs", "RpcEptMapper", "DcomLaunch", "LSM", "SamSs", "EventLog", "PlugPlay", "Power", "BFE", "MpsSvc",
        "WinDefend", "wuauserv", "TrustedInstaller", "msiserver", "Schedule", "Winmgmt", "CryptSvc", "Dnscache",
        "Dhcp", "nsi", "ProfSvc", "UserManager", "gpsvc", "Themes", "AudioSrv", "Audiosrv", "SystemEventsBroker",
        "BrokerInfrastructure", "CoreMessagingRegistrar", "StateRepository", "AppXSvc", "ClipSVC", "LanmanServer",
        "LanmanWorkstation", "W32Time", "VSS", "swprv", "SENS", "ShellHWDetection", "Spooler", "TokenBroker",
    };

    public static bool IsProtected(ServiceInfo service)
    {
        if (CriticalNames.Contains(service.Name))
        {
            return true;
        }

        // Driver (kernel, sistem berkas, adapter, recognizer) tidak pernah dihapus otomatis.
        if ((service.Type & (KernelDriver | FileSystemDriver | Adapter | RecognizerDriver)) != 0)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(service.ImagePath) || !Path.IsPathFullyQualified(service.ImagePath))
        {
            return true;
        }

        // Layanan bawaan Windows (svchost dan exe di folder Windows).
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return service.ImagePath.StartsWith(Path.TrimEndingDirectorySeparator(windows) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Tugas di \Microsoft\ (dan akar) tidak pernah dihapus.</summary>
    public static bool IsProtectedTask(string? taskPath) =>
        string.IsNullOrWhiteSpace(taskPath)
        || taskPath.Trim() == @"\"
        || taskPath.TrimStart('\\').StartsWith(@"Microsoft\", StringComparison.OrdinalIgnoreCase)
        || taskPath.TrimStart('\\').Equals("Microsoft", StringComparison.OrdinalIgnoreCase);
}

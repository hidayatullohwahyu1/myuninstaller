using Microsoft.Win32;

namespace MyUninstaller.Infrastructure.Leftovers;

/// <summary>Satu lokasi registri yang dibaca: hive + path subkunci + path tampilan lengkap.</summary>
public sealed record RegistryRoot(RegistryHive Hive, string SubKey)
{
    public string FullPath => (Hive switch
    {
        RegistryHive.LocalMachine => "HKEY_LOCAL_MACHINE",
        RegistryHive.CurrentUser => "HKEY_CURRENT_USER",
        _ => "HKEY_USERS",
    }) + @"\" + SubKey;
}

/// <summary>
/// Lokasi yang ditelusuri pemindai sisa. Bawaan = lokasi sistem sungguhan; tes membuat versi sendiri
/// (folder sementara dan kunci HKCU uji).
/// </summary>
public sealed record LeftoverScanRoots
{
    /// <summary>Folder yang anak-anaknya diperiksa namanya (Program Files, ProgramData, AppData, Start Menu...).</summary>
    public required IReadOnlyList<string> FolderRoots { get; init; }

    /// <summary>Folder data pengguna (Dokumen): kecocokan nama hanya "Mungkin" karena bisa berisi karya pengguna.</summary>
    public required IReadOnlyList<string> UserDataRoots { get; init; }

    /// <summary>Folder berisi pintasan (.lnk), ditelusuri rekursif.</summary>
    public required IReadOnlyList<string> ShortcutRoots { get; init; }

    /// <summary>Kunci SOFTWARE yang anak-anaknya diperiksa namanya.</summary>
    public required IReadOnlyList<RegistryRoot> SoftwareRoots { get; init; }

    /// <summary>Kunci Run/RunOnce.</summary>
    public required IReadOnlyList<RegistryRoot> RunKeys { get; init; }

    /// <summary>Kunci App Paths.</summary>
    public required IReadOnlyList<RegistryRoot> AppPathsKeys { get; init; }

    /// <summary>Kunci CLSID (mode Super).</summary>
    public required IReadOnlyList<RegistryRoot> ClsidKeys { get; init; }

    /// <summary>Kunci Classes (asosiasi berkas: ProgID, Applications\exe, .ext\OpenWithProgids).</summary>
    public IReadOnlyList<RegistryRoot> ClassesKeys { get; init; } = [];

    /// <summary>Kunci variabel lingkungan pengguna (PATH), null untuk melewati. Hanya HKCU.</summary>
    public RegistryRoot? EnvironmentKey { get; init; }

    /// <summary>Kunci layanan (mode Super), null untuk melewati.</summary>
    public RegistryRoot? ServicesKey { get; init; }

    /// <summary>Folder definisi tugas terjadwal (mode Super), null untuk melewati.</summary>
    public string? TasksFolder { get; init; }

    /// <summary>Akar drive untuk pencarian nama (mode Super).</summary>
    public required IReadOnlyList<string> DriveRoots { get; init; }

    public static LeftoverScanRoots ForCurrentSystem()
    {
        static string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        const string Run = @"Microsoft\Windows\CurrentVersion\Run";
        const string RunOnce = @"Microsoft\Windows\CurrentVersion\RunOnce";
        const string AppPaths = @"Microsoft\Windows\CurrentVersion\App Paths";

        string local = F(Environment.SpecialFolder.LocalApplicationData);
        string userProfile = F(Environment.SpecialFolder.UserProfile);

        return new LeftoverScanRoots
        {
            FolderRoots =
            [
                F(Environment.SpecialFolder.ProgramFiles),
                F(Environment.SpecialFolder.ProgramFilesX86),
                F(Environment.SpecialFolder.CommonApplicationData),
                F(Environment.SpecialFolder.ApplicationData),
                local,
                Path.Combine(local, "Programs"),
                Path.Combine(userProfile, "AppData", "LocalLow"),
                F(Environment.SpecialFolder.Programs),
                F(Environment.SpecialFolder.CommonPrograms),
            ],
            UserDataRoots =
            [
                F(Environment.SpecialFolder.MyDocuments),
                F(Environment.SpecialFolder.CommonDocuments),
                userProfile, // misalnya C:\Users\Budi\.vscode
            ],
            ShortcutRoots =
            [
                F(Environment.SpecialFolder.DesktopDirectory),
                F(Environment.SpecialFolder.CommonDesktopDirectory),
                F(Environment.SpecialFolder.Programs),
                F(Environment.SpecialFolder.CommonPrograms),
                F(Environment.SpecialFolder.Startup),
                F(Environment.SpecialFolder.CommonStartup),
            ],
            SoftwareRoots =
            [
                new(RegistryHive.LocalMachine, "SOFTWARE"),
                new(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node"),
                new(RegistryHive.CurrentUser, "Software"),
            ],
            RunKeys =
            [
                new(RegistryHive.LocalMachine, $@"SOFTWARE\{Run}"),
                new(RegistryHive.LocalMachine, $@"SOFTWARE\{RunOnce}"),
                new(RegistryHive.LocalMachine, $@"SOFTWARE\WOW6432Node\{Run}"),
                new(RegistryHive.LocalMachine, $@"SOFTWARE\WOW6432Node\{RunOnce}"),
                new(RegistryHive.CurrentUser, $@"Software\{Run}"),
                new(RegistryHive.CurrentUser, $@"Software\{RunOnce}"),
            ],
            AppPathsKeys =
            [
                new(RegistryHive.LocalMachine, $@"SOFTWARE\{AppPaths}"),
                new(RegistryHive.LocalMachine, $@"SOFTWARE\WOW6432Node\{AppPaths}"),
                new(RegistryHive.CurrentUser, $@"Software\{AppPaths}"),
            ],
            ClsidKeys =
            [
                new(RegistryHive.LocalMachine, @"SOFTWARE\Classes\CLSID"),
                new(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Classes\CLSID"),
                new(RegistryHive.CurrentUser, @"Software\Classes\CLSID"),
            ],
            ClassesKeys =
            [
                new(RegistryHive.CurrentUser, @"Software\Classes"),
                new(RegistryHive.LocalMachine, @"SOFTWARE\Classes"),
            ],
            EnvironmentKey = new(RegistryHive.CurrentUser, "Environment"),
            ServicesKey = new(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services"),
            TasksFolder = Path.Combine(F(Environment.SpecialFolder.System), "Tasks"),
            DriveRoots = [.. DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName)],
        };
    }
}

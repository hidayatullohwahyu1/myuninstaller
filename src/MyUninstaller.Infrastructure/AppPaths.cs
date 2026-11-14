namespace MyUninstaller.Infrastructure;

/// <summary>Lokasi data aplikasi.</summary>
public sealed class AppPaths
{
    public const string AppFolderName = "MyUninstaller";

    public AppPaths()
        : this(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppFolderName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName))
    {
    }

    public AppPaths(string dataRoot, string userRoot)
    {
        DataRoot = dataRoot;
        UserRoot = userRoot;
    }

    /// <summary>%ProgramData%\MyUninstaller: riwayat, karantina, log.</summary>
    public string DataRoot { get; }

    /// <summary>%LocalAppData%\MyUninstaller: pengaturan tampilan per pengguna.</summary>
    public string UserRoot { get; }

    public string LogsDirectory => Path.Combine(DataRoot, "Log");

    public string QuarantineDirectory => Path.Combine(DataRoot, "Karantina");

    public string DatabaseFile => Path.Combine(DataRoot, "riwayat.db");

    public string SettingsFile => Path.Combine(UserRoot, "pengaturan.json");
}

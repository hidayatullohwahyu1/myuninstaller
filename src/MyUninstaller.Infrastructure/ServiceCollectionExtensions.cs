using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyUninstaller.Core.Abstractions;
using MyUninstaller.Core.Browsers;
using MyUninstaller.Core.Leftovers;
using MyUninstaller.Core.Scanning;
using MyUninstaller.Core.Startup;
using MyUninstaller.Core.Usage;
using MyUninstaller.Core.Uninstall;
using MyUninstaller.Core.Safety;
using MyUninstaller.Infrastructure.Processes;
using MyUninstaller.Infrastructure.Safety;
using MyUninstaller.Infrastructure.Browsers;
using MyUninstaller.Infrastructure.FileSystem;
using MyUninstaller.Infrastructure.Leftovers;
using MyUninstaller.Infrastructure.WindowsRegistry;
using MyUninstaller.Infrastructure.Scanning;
using MyUninstaller.Infrastructure.Settings;
using MyUninstaller.Infrastructure.Startup;
using MyUninstaller.Infrastructure.Usage;
using MyUninstaller.Infrastructure.Store;
using MyUninstaller.Infrastructure.Theming;

namespace MyUninstaller.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>Mendaftarkan layanan lapisan Infrastruktur.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.TryAddSingleton<AppPaths>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<IWindowsThemeReader, WindowsThemeReader>();

        // Pemindai program (Tahap 2)
        services.AddSingleton<IRegistryUninstallReader, RegistryUninstallReader>();
        services.AddSingleton<IStorePackageReader, StorePackageReader>();
        services.AddSingleton<IProgramScanner, ProgramScanner>();
        services.AddSingleton<IIconPathResolver, IconPathResolver>();
        services.AddSingleton<IFolderSizeCalculator, FolderSizeCalculator>();
        services.AddSingleton(_ => InstallLocationRules.ForCurrentSystem());

        // Hapus instalan (Tahap 3)
        services.AddSingleton<IProcessRunner, JobProcessRunner>();
        services.AddSingleton<IStorePackageRemover, StorePackageRemover>();
        services.AddSingleton<IInstallStateProbe, InstallStateProbe>();
        services.AddSingleton<IProcessInspector, ProcessInspector>();
        services.AddSingleton<IInstallerKindDetector, InstallerKindDetector>();
        services.AddSingleton<UninstallRunner>();

        // Keamanan (Tahap 4): daftar lindung, jurnal, karantina, cadangan registri, titik pemulihan
        services.AddSingleton(sp => ProtectedPaths.ForCurrentSystem(sp.GetRequiredService<AppPaths>().DataRoot));
        services.AddSingleton<ISessionJournal, SqliteSessionJournal>();
        services.AddSingleton<IQuarantineService, QuarantineService>();
        services.AddSingleton<IRegistryBackupService, RegistryBackupService>();
        services.AddSingleton<IRestorePointService, RestorePointService>();
        services.AddSingleton<IServiceRemover, ServiceRemover>();
        services.AddSingleton<IScheduledTaskRemover, ScheduledTaskRemover>();
        services.AddSingleton<SafeRemover>();

        // Program awal dan riwayat pemakaian (versi 1.2)
        services.AddSingleton<IStartupRepository, StartupRepository>();
        services.AddSingleton<IUsageHistory, UsageHistoryReader>();

        // Ekstensi browser (versi 1.3)
        services.AddSingleton<IBrowserExtensionReader, BrowserExtensionReader>();
        services.AddSingleton<IUnelevatedLauncher, DesktopShellLauncher>();

        // Pemindai sisa (Tahap 5)
        services.AddSingleton<ILeftoverScanner, LeftoverScanner>();
        services.AddSingleton<SessionRestorer>();

        return services;
    }
}

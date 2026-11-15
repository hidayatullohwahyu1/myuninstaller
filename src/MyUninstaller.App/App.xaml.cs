using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyUninstaller.App.Interop;
using MyUninstaller.App.Localization;
using MyUninstaller.App.Services;
using MyUninstaller.App.ViewModels;
using MyUninstaller.App.Views;
using MyUninstaller.Core.Shell;
using MyUninstaller.Infrastructure;
using Serilog;

namespace MyUninstaller.App;

public partial class App : Application
{
    // Per sesi pengguna: dua jendela My Uninstaller! tidak boleh berjalan bersamaan.
    private const string SingleInstanceMutexName = @"Local\MyUninstaller.SingleInstance.{B8E5D2A4-6C1F-4E7B-9A3D-2F8C1E5B7A90}";

    private Mutex? _singleInstanceMutex;
    private IHost? _host;
    private InstanceChannel? _channel;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        string? shellTarget = ShellRequest.ParseArguments(e.Args);
        if (!createdNew)
        {
            // Menu klik kanan saat aplikasi sudah terbuka: teruskan berkasnya ke instance pertama.
            if (shellTarget is not null)
            {
                InstanceChannel.Send(InstanceChannel.DefaultPipeName, shellTarget);
            }

            NativeMethods.ActivateOtherInstance();
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown(0);
            return;
        }

        ConfigureCulture();

        var paths = new AppPaths();
        ConfigureLogging(paths);
        RegisterGlobalExceptionHandlers();

        Log.Information("My Uninstaller! {Version} dimulai (Windows {OsVersion}, 64-bit: {Is64})",
            typeof(App).Assembly.GetName().Version, Environment.OSVersion.Version, Environment.Is64BitOperatingSystem);

        base.OnStartup(e);

        try
        {
            var startup = System.Diagnostics.Stopwatch.StartNew();
            _host = BuildHost(paths);
            _host.Start();

            // Pindai program sejajar dengan pembuatan jendela utama (daftar tampil lebih cepat).
            _host.Services.GetRequiredService<InitialScan>().Start();

            _host.Services.GetRequiredService<ThemeService>().Initialize();
            long hostMs = startup.ElapsedMilliseconds;

            var window = _host.Services.GetRequiredService<MainWindow>();
            long windowMs = startup.ElapsedMilliseconds;
            MainWindow = window;
            var viewModel = _host.Services.GetRequiredService<MainViewModel>();
            window.Show();

            // Setelah jendela tampil: tawarkan pemulihan sesi terputus, lalu bersihkan karantina kedaluwarsa.
            var maintenance = _host.Services.GetRequiredService<StartupMaintenance>();
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                maintenance.OfferRecovery(window);
                _ = maintenance.PurgeQuarantineAsync();
                if (shellTarget is not null)
                {
                    _ = viewModel.UninstallFromShellAsync(shellTarget);
                }
            });

            // Permintaan menu klik kanan berikutnya datang lewat pipe dari instance kedua.
            _channel = new InstanceChannel(InstanceChannel.DefaultPipeName, InstanceChannel.AdministratorsOnly,
                _host.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<InstanceChannel>>());
            _ = _channel.ListenAsync(path => Dispatcher.BeginInvoke(() =>
            {
                if (window.WindowState == WindowState.Minimized)
                {
                    window.WindowState = WindowState.Normal;
                }

                window.Activate();
                _ = viewModel.UninstallFromShellAsync(path);
            }));
            Log.Information("Startup: layanan {HostMs} ms, jendela dibuat {WindowMs} ms, tampil {ShownMs} ms",
                hostMs, windowMs, startup.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Gagal memulai aplikasi");
            MessageBox.Show(Strings.Format("Error_Fatal", ex.Message), Strings.Get("Error_Title"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _channel?.Dispose();
            if (_host is not null)
            {
                _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                _host.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Galat saat menutup layanan");
        }
        finally
        {
            Log.Information("My Uninstaller! ditutup (kode {ExitCode})", e.ApplicationExitCode);
            Log.CloseAndFlush();

            if (_singleInstanceMutex is not null)
            {
                _singleInstanceMutex.ReleaseMutex();
                _singleInstanceMutex.Dispose();
            }
        }

        base.OnExit(e);
    }

    private static IHost BuildHost(AppPaths paths)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            // Tanpa appsettings.json, variabel lingkungan, atau log konsol: semua log ke Serilog.
            DisableDefaults = true,
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = "MyUninstaller",
        });

        builder.Services.AddSerilog(dispose: false);
        builder.Services.AddSingleton(paths);
        builder.Services.AddInfrastructure();

        builder.Services.AddSingleton<ThemeService>();
        builder.Services.AddSingleton<IDialogService, DialogService>();
        builder.Services.AddSingleton<IShellService, ShellService>();
        builder.Services.AddSingleton<IconLoader>();
        builder.Services.AddSingleton<UninstallWorkflow>();
        builder.Services.AddSingleton<HunterProbe>();
        builder.Services.AddSingleton<StartupMaintenance>();
        builder.Services.AddSingleton<HistoryService>();
        builder.Services.AddSingleton<InitialScan>();
        builder.Services.AddSingleton<BrowserExtensionService>();
        builder.Services.AddSingleton<PortableProgramService>();
        // Lazy<T>: layanan berat dibuat saat pertama dipakai (lihat LazyService).
        builder.Services.AddTransient(typeof(Lazy<>), typeof(LazyService<>));
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        return builder.Build();
    }

    private static void ConfigureCulture()
    {
        // Angka, tanggal, dan perbandingan teks memakai aturan bahasa Indonesia.
        CultureInfo culture = Strings.Culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;

        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));
    }

    private static void ConfigureLogging(AppPaths paths)
    {
        try
        {
            Directory.CreateDirectory(paths.LogsDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sink berkas akan gagal diam-diam; aplikasi tetap berjalan.
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(paths.LogsDirectory, "myuninstaller-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                formatProvider: CultureInfo.InvariantCulture,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    private void RegisterGlobalExceptionHandlers()
    {
        // Galat di utas UI: catat, beri tahu, dan tetap berjalan.
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Galat di utas lain: proses akan berhenti; pastikan tercatat dulu.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception, "Galat tak tertangani (terminating: {IsTerminating})", args.IsTerminating);
            Log.CloseAndFlush();
        };

        // Task yang galatnya tidak pernah ditunggu.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Galat Task yang tidak diamati");
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Galat tak tertangani di utas UI");
        e.Handled = true;

        try
        {
            MessageBox.Show(Strings.Format("Error_Unexpected", e.Exception.Message), Strings.Get("Error_Title"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Gagal menampilkan pesan galat");
        }
    }
}

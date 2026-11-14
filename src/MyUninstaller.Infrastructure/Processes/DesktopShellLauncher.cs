using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using MyUninstaller.Core.Browsers;

namespace MyUninstaller.Infrastructure.Processes;

/// <summary>Menjalankan program sebagai pengguna biasa (tidak ikut hak Administrator aplikasi ini).</summary>
public interface IUnelevatedLauncher
{
    /// <summary>Menjalankan <paramref name="file"/> dengan argumen lewat Explorer. False bila gagal.</summary>
    bool Launch(string file, string arguments);

    /// <summary>Path exe browser dari App Paths registri, atau null bila tidak terpasang.</summary>
    string? FindBrowser(BrowserKind browser);
}

/// <summary>
/// Aplikasi ini berjalan sebagai Administrator; browser yang dibuka langsung akan ikut berhak admin (berbahaya).
/// Solusi standar Windows: minta Explorer (yang berjalan sebagai pengguna biasa) menjalankan program lewat
/// IShellDispatch2.ShellExecute milik tampilan desktop.
/// </summary>
public sealed class DesktopShellLauncher(ILogger<DesktopShellLauncher> logger) : IUnelevatedLauncher
{
    private const int SwcDesktop = 8;
    private const int SwfoNeedDispatch = 1;
    private const uint SvgioBackground = 0;

    private static readonly Guid ClsidShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid SidTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IidShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid IidDispatch = new("00020400-0000-0000-C000-000000000046");

    public bool Launch(string file, string arguments) => Launch(file, arguments, showCommand: 1);

    /// <param name="showCommand">SW_*: 1 = normal, 0 = tersembunyi (untuk tes).</param>
    internal bool Launch(string file, string arguments, int showCommand)
    {
        object? windows = null;
        try
        {
            windows = Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidShellWindows, throwOnError: true)!)!;
            object location = 0; // CSIDL_DESKTOP
            object empty = new();
            object desktop = ((IShellWindows)windows).FindWindowSW(ref location, ref empty, SwcDesktop, out _, SwfoNeedDispatch);

            Guid sid = SidTopLevelBrowser, iid = IidShellBrowser;
            var browser = (IShellBrowser)((IServiceProvider)desktop).QueryService(ref sid, ref iid);
            browser.QueryActiveShellView(out IShellView view);

            Guid dispatchId = IidDispatch;
            view.GetItemObject(SvgioBackground, ref dispatchId, out object folderView);

            dynamic shell = ((dynamic)folderView).Application;
            shell.ShellExecute(file, arguments, string.Empty, "open", showCommand);
            logger.LogInformation("Dijalankan sebagai pengguna biasa: {File} {Arguments}", file, arguments);
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or ArgumentException or NotSupportedException)
        {
            logger.LogWarning(ex, "Gagal menjalankan {File} lewat Explorer", file);
            return false;
        }
        finally
        {
            if (windows is not null)
            {
                Marshal.FinalReleaseComObject(windows);
            }
        }
    }

    public string? FindBrowser(BrowserKind browser)
    {
        string exe = browser switch
        {
            BrowserKind.Chrome => "chrome.exe",
            BrowserKind.Edge => "msedge.exe",
            BrowserKind.Brave => "brave.exe",
            _ => "firefox.exe",
        };

        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var key = hive.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}", writable: false);
            if (key?.GetValue(null) is string path && File.Exists(path.Trim('"')))
            {
                return path.Trim('"');
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ antarmuka COM (urutan vtable penting)

    [ComImport]
    [Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        int Count { get; }

        void Item();

        void NewEnum();

        void Register();

        void RegisterPending();

        void Revoke();

        void OnNavigate();

        void OnActivated();

        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW(
            [In, MarshalAs(UnmanagedType.Struct)] ref object location,
            [In, MarshalAs(UnmanagedType.Struct)] ref object locationRoot,
            int windowClass,
            out int windowHandle,
            int options);
    }

    [ComImport]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object QueryService(ref Guid service, ref Guid riid);
    }

    [ComImport]
    [Guid("000214E2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow();

        void ContextSensitiveHelp();

        void InsertMenusSB();

        void SetMenuSB();

        void RemoveMenusSB();

        void SetStatusTextSB();

        void EnableModelessSB();

        void TranslateAcceleratorSB();

        void BrowseObject();

        void GetViewStateStream();

        void GetControlWindow();

        void SendControlMsg();

        void QueryActiveShellView(out IShellView view);
    }

    [ComImport]
    [Guid("000214E3-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void GetWindow();

        void ContextSensitiveHelp();

        void TranslateAccelerator();

        void EnableModeless();

        void UIActivate();

        void Refresh();

        void CreateViewWindow();

        void DestroyViewWindow();

        void GetCurrentInfo();

        void AddPropertySheetPages();

        void SaveViewState();

        void SelectItem();

        void GetItemObject(uint item, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    }
}

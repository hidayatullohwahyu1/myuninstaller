using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Microsoft.Extensions.Logging;
using MyUninstaller.Core.Hunter;
using MyUninstaller.Core.Models;
using MyUninstaller.Infrastructure.FileSystem;
using MyUninstaller.Infrastructure.Processes;

namespace MyUninstaller.App.Services;

/// <summary>Hasil Mode Pemburu.</summary>
/// <param name="Description">Apa yang ditunjuk (judul jendela, nama pintasan, atau tooltip ikon baki).</param>
/// <param name="ExecutablePath">Exe yang terkait, bila diketahui.</param>
/// <param name="Program">Program terpasang yang cocok, atau null.</param>
public sealed record HunterResult(string Description, string? ExecutablePath, InstalledProgram? Program);

/// <summary>
/// Mode Pemburu: mengenali program dari titik di layar (jendela, ikon desktop, tombol bilah tugas,
/// ikon baki) atau dari berkas pintasan/exe yang dijatuhkan. Hanya membaca; tidak menyuntik apa pun.
/// </summary>
public sealed partial class HunterProbe(ILogger<HunterProbe> logger)
{
    public HunterResult? IdentifyAtCursor(IReadOnlyList<InstalledProgram> programs)
    {
        if (!GetCursorPos(out Point point))
        {
            return null;
        }

        IntPtr hwnd = WindowFromPoint(point);
        IntPtr root = hwnd == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hwnd, GaRoot);
        if (root == IntPtr.Zero)
        {
            return null;
        }

        GetWindowThreadProcessId(root, out uint pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId)
        {
            return null;
        }

        string? exe = ProcessInspector.GetExecutablePath((int)pid);
        string title = GetWindowTitle(root);

        // explorer.exe = desktop, bilah tugas, atau baki: kenali elemen yang ditunjuk lewat UI Automation.
        if (exe is not null && IsExplorer(exe))
        {
            return IdentifyShellElement(point, programs);
        }

        var program = ProgramMatcher.FindByExecutable(exe, programs);
        return new HunterResult(string.IsNullOrWhiteSpace(title) ? Path.GetFileName(exe) ?? "?" : title, exe, program);
    }

    /// <summary>Berkas yang dijatuhkan ke jendela utama: .lnk, .exe, atau folder.</summary>
    public HunterResult IdentifyFile(string path, IReadOnlyList<InstalledProgram> programs)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        string? exe = path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutResolver.GetTarget(path) : path;

        var program = ProgramMatcher.FindByExecutable(exe, programs)
                      ?? ProgramMatcher.FindByName(name, programs);
        return new HunterResult(name, exe, program);
    }

    private HunterResult? IdentifyShellElement(Point point, IReadOnlyList<InstalledProgram> programs)
    {
        try
        {
            AutomationElement element = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y));
            string name = element.Current.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            // Ikon desktop: cari berkas pintasan bernama sama di folder Desktop pengguna dan publik.
            if (element.Current.ControlType == ControlType.ListItem && FindDesktopFile(name) is { } file)
            {
                return IdentifyFile(file, programs);
            }

            // Tombol bilah tugas / ikon baki: cocokkan nama (tooltip) dengan nama program.
            return new HunterResult(name, null, ProgramMatcher.FindByName(name, programs));
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            logger.LogDebug(ex, "UI Automation gagal di titik {X},{Y}", point.X, point.Y);
            return null;
        }
    }

    private static string? FindDesktopFile(string name)
    {
        string[] folders =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        ];

        foreach (string folder in folders.Where(Directory.Exists))
        {
            foreach (string file in Directory.EnumerateFiles(folder))
            {
                if (Path.GetFileNameWithoutExtension(file).Equals(name, StringComparison.CurrentCultureIgnoreCase)
                    || Path.GetFileName(file).Equals(name, StringComparison.CurrentCultureIgnoreCase))
                {
                    return file;
                }
            }
        }

        return null;
    }

    private static bool IsExplorer(string exe) =>
        exe.Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), StringComparison.OrdinalIgnoreCase);

    private static unsafe string GetWindowTitle(IntPtr hwnd)
    {
        char* buffer = stackalloc char[512];
        int length = GetWindowTextW(hwnd, buffer, 512);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    private const uint GaRoot = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    private static partial IntPtr WindowFromPoint(Point point);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [LibraryImport("user32.dll")]
    private static unsafe partial int GetWindowTextW(IntPtr hwnd, char* text, int maxCount);
}

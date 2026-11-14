using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MyUninstaller.Core.Safety;
using MyUninstaller.Core.Uninstall;

namespace MyUninstaller.Infrastructure.Processes;

/// <summary>Daftar proses beserta path exe-nya, dan penutupan paksa proses non-kritis.</summary>
public sealed partial class ProcessInspector(ILogger<ProcessInspector> logger) : IProcessInspector
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    public IReadOnlyList<RunningProcess> GetRunningProcesses()
    {
        var result = new List<RunningProcess>(256);
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    result.Add(new RunningProcess(process.Id, process.ProcessName, GetExecutablePath(process.Id)));
                }
                catch (InvalidOperationException)
                {
                    // Proses sudah keluar.
                }
            }
        }

        return result;
    }

    public bool TryKill(RunningProcess process)
    {
        if (RunningProcessRules.IsCritical(process) || process.ProcessId == Environment.ProcessId)
        {
            logger.LogWarning("Menolak menutup proses kritis {Name} ({Pid})", process.Name, process.ProcessId);
            return false;
        }

        try
        {
            using var p = Process.GetProcessById(process.ProcessId);

            // Pastikan PID belum dipakai ulang oleh proses lain.
            if (!string.Equals(GetExecutablePath(p.Id), process.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            p.Kill(entireProcessTree: false);
            bool exited = p.WaitForExit(5000);
            logger.LogInformation("Proses {Name} ({Pid}) ditutup paksa: {Exited}", process.Name, process.ProcessId, exited);
            return exited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            logger.LogWarning(ex, "Gagal menutup proses {Name} ({Pid})", process.Name, process.ProcessId);
            return false;
        }
    }

    /// <summary>Path exe proses (QueryFullProcessImageName), atau null bila tidak bisa dibaca.</summary>
    public static string? GetExecutablePath(int processId)
    {
        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            unsafe
            {
                char* buffer = stackalloc char[1024];
                uint size = 1024;
                return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageNameW(IntPtr hProcess, uint flags, char* exeName, ref uint size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}

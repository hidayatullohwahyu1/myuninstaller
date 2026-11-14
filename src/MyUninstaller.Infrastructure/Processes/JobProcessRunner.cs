using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MyUninstaller.Core.Uninstall;

namespace MyUninstaller.Infrastructure.Processes;

/// <summary>
/// Menjalankan penghapus instalan di dalam Job Object supaya proses anak (misalnya unins000.exe yang
/// menyalin diri ke %TEMP% lalu keluar) ikut ditunggu sampai semuanya selesai.
/// </summary>
public sealed partial class JobProcessRunner(ILogger<JobProcessRunner> logger) : IProcessRunner
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public async Task<ProcessRunResult> RunAsync(UninstallCommand command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        string file = ResolveFile(command.FileName);
        string commandLine = string.IsNullOrWhiteSpace(command.Arguments) ? $"\"{file}\"" : $"\"{file}\" {command.Arguments}";
        string? workingDirectory = Path.GetDirectoryName(file) is { } dir && Directory.Exists(dir) ? dir : null;

        logger.LogInformation("Menjalankan penghapus instalan: {CommandLine}", commandLine);
        var stopwatch = Stopwatch.StartNew();

        IntPtr job = CreateJob();
        try
        {
            ProcessInformation info;
            try
            {
                info = StartSuspendedInJob(commandLine, workingDirectory, job);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorElevationRequired)
            {
                // Hanya terjadi bila aplikasi tidak berjalan sebagai admin (mode pengembangan).
                logger.LogWarning("Penghapus instalan butuh elevasi; dijalankan lewat ShellExecute tanpa Job Object");
                return await RunElevatedFallbackAsync(file, command.Arguments, workingDirectory, timeout, stopwatch, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                return await WaitForJobAsync(job, info.hProcess, timeout, stopwatch, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CloseHandle(info.hThread);
                CloseHandle(info.hProcess);
            }
        }
        finally
        {
            CloseHandle(job);
        }
    }

    private async Task<ProcessRunResult> WaitForJobAsync(
        IntPtr job, IntPtr process, TimeSpan timeout, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        bool cancelled = false;
        bool timedOut = false;

        while (ActiveProcessCount(job) > 0)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // Tombol Hentikan: hentikan seluruh pohon proses penghapus instalan.
                logger.LogWarning("Penghapus instalan dihentikan pengguna");
                TerminateJobObject(job, 1);
                cancelled = true;
                break;
            }

            if (stopwatch.Elapsed > timeout)
            {
                // Proses dibiarkan berjalan; jangan matikan uninstaller yang mungkin sedang menulis.
                logger.LogWarning("Penghapus instalan melewati batas waktu {Timeout}", timeout);
                timedOut = true;
                break;
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Ditangani di awal putaran berikutnya.
            }
        }

        int? exitCode = !cancelled && !timedOut && GetExitCodeProcess(process, out uint code) && code != StillActive
            ? unchecked((int)code)
            : null;

        logger.LogInformation("Penghapus instalan selesai: kode {ExitCode}, {Elapsed} detik", exitCode, (int)stopwatch.Elapsed.TotalSeconds);
        return new ProcessRunResult(exitCode, timedOut, cancelled, stopwatch.Elapsed);
    }

    private static async Task<ProcessRunResult> RunElevatedFallbackAsync(
        string file, string arguments, string? workingDirectory, TimeSpan timeout, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = workingDirectory ?? string.Empty,
            });
            if (process is null)
            {
                return new ProcessRunResult(null, false, false, stopwatch.Elapsed);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                return new ProcessRunResult(process.ExitCode, false, false, stopwatch.Elapsed);
            }
            catch (OperationCanceledException)
            {
                return new ProcessRunResult(null, !cancellationToken.IsCancellationRequested, cancellationToken.IsCancellationRequested, stopwatch.Elapsed);
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            // Pengguna menolak UAC.
            return new ProcessRunResult(null, false, true, stopwatch.Elapsed);
        }
    }

    /// <summary>msiexec.exe dan cmd.exe selalu dari System32, bukan dari PATH.</summary>
    private static string ResolveFile(string fileName)
    {
        if (Path.IsPathFullyQualified(fileName))
        {
            return fileName;
        }

        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string candidate = Path.Combine(system, fileName);
        return File.Exists(candidate) ? candidate : fileName;
    }

    // ------------------------------------------------------------------ Win32

    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNewConsole = 0x00000010;
    private const int ErrorElevationRequired = 740;
    private const int ErrorCancelled = 1223;
    private const uint StillActive = 259;
    private const int JobObjectBasicAccountingInformation = 1;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitBreakawayOk = 0x00000800;

    private static IntPtr CreateJob()
    {
        IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        // Izinkan proses yang meminta CREATE_BREAKAWAY_FROM_JOB keluar dari job, agar uninstaller
        // seperti itu tidak gagal. JANGAN pakai KILL_ON_JOB_CLOSE: bila aplikasi ini tertutup,
        // penghapus instalan harus tetap selesai.
        var limits = new JobObjectExtendedLimitInformationStruct();
        limits.BasicLimitInformation.LimitFlags = JobObjectLimitBreakawayOk;
        unsafe
        {
            SetInformationJobObject(job, JobObjectExtendedLimitInformation, &limits, (uint)sizeof(JobObjectExtendedLimitInformationStruct));
        }

        return job;
    }

    private static unsafe ProcessInformation StartSuspendedInJob(string commandLine, string? workingDirectory, IntPtr job)
    {
        var startup = new StartupInfo { cb = (uint)sizeof(StartupInfo) };
        char[] buffer = (commandLine + '\0').ToCharArray(); // CreateProcessW boleh menulis ke buffer ini.

        ProcessInformation info;
        fixed (char* cmd = buffer)
        {
            if (!CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                    CreateSuspended | CreateUnicodeEnvironment | CreateNewConsole,
                    IntPtr.Zero, workingDirectory, ref startup, out info))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }

        if (!AssignProcessToJobObject(job, info.hProcess))
        {
            // Tetap jalan tanpa pelacakan anak; jangan biarkan proses tergantung dalam keadaan suspended.
            Debug.WriteLine($"AssignProcessToJobObject gagal: {Marshal.GetLastPInvokeError()}");
        }

        ResumeThread(info.hThread);
        return info;
    }

    private static unsafe uint ActiveProcessCount(IntPtr job)
    {
        JobObjectBasicAccountingInformationStruct accounting;
        return QueryInformationJobObject(job, JobObjectBasicAccountingInformation, &accounting,
            (uint)sizeof(JobObjectBasicAccountingInformationStruct), IntPtr.Zero)
            ? accounting.ActiveProcesses
            : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformationStruct
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationStruct
    {
        public JobObjectBasicLimitInformationStruct BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccountingInformationStruct
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetInformationJobObject(IntPtr hJob, int infoClass, void* info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryInformationJobObject(IntPtr hJob, int infoClass, void* info, uint length, IntPtr returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(IntPtr hJob, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcessW(
        string? lpApplicationName,
        char* lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfo lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(IntPtr hThread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}

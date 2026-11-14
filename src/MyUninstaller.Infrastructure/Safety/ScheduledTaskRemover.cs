using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MyUninstaller.Core.Safety;

namespace MyUninstaller.Infrastructure.Safety;

/// <summary>
/// Tugas terjadwal. Definisinya disalin langsung dari C:\Windows\System32\Tasks (XML asli, UTF-16),
/// dihapus dan dipulihkan dengan schtasks.exe.
/// </summary>
public sealed class ScheduledTaskRemover(string backupRoot, ILogger<ScheduledTaskRemover> logger) : IScheduledTaskRemover
{
    public ScheduledTaskRemover(AppPaths paths, ILogger<ScheduledTaskRemover> logger)
        : this(paths.QuarantineDirectory, logger)
    {
    }

    public static string TasksFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks");

    public bool Exists(string taskPath) => File.Exists(TaskFile(taskPath));

    public string? Export(string sessionId, string taskPath)
    {
        try
        {
            string folder = Path.Combine(backupRoot, sessionId, "tugas");
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, string.Join('_', taskPath.Trim('\\').Split('\\')) + ".xml");
            File.Copy(TaskFile(taskPath), file, overwrite: true);
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Ekspor tugas {Task} gagal", taskPath);
            return null;
        }
    }

    public bool Delete(string taskPath) => !ProtectedServices.IsProtectedTask(taskPath) && Run($"/delete /tn \"{taskPath}\" /f");

    public bool Import(string xmlFile, string taskPath) => File.Exists(xmlFile) && Run($"/create /tn \"{taskPath}\" /xml \"{xmlFile}\" /f");

    private static string TaskFile(string taskPath) => Path.Combine(TasksFolder, taskPath.TrimStart('\\'));

    private bool Run(string arguments)
    {
        string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                process.Kill();
                return false;
            }

            if (process.ExitCode != 0)
            {
                logger.LogWarning("schtasks {Arguments} gagal: {Error}", arguments, (output.Result + error.Result).Trim());
            }

            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "schtasks.exe gagal dijalankan");
            return false;
        }
    }
}

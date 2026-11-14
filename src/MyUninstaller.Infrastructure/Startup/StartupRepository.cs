using System.Diagnostics;
using System.Security;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using MyUninstaller.Core.Abstractions;
using MyUninstaller.Core.Safety;
using MyUninstaller.Core.Scanning;
using MyUninstaller.Core.Startup;
using MyUninstaller.Infrastructure.FileSystem;
using MyUninstaller.Infrastructure.Leftovers;

namespace MyUninstaller.Infrastructure.Startup;

/// <summary>Satu lokasi Run beserta kunci StartupApproved pasangannya.</summary>
public sealed record RunLocation(StartupSource Source, RegistryRoot Run, RegistryRoot Approved);

/// <summary>Satu folder Startup beserta kunci StartupApproved pasangannya.</summary>
public sealed record FolderLocation(StartupSource Source, string Folder, RegistryRoot Approved);

/// <summary>Lokasi program awal. Bawaan = lokasi sistem; tes memakai kunci HKCU uji dan folder sementara.</summary>
public sealed record StartupLocations(
    IReadOnlyList<RunLocation> RunKeys,
    IReadOnlyList<FolderLocation> Folders,
    string? TasksFolder)
{
    private const string Run = @"Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public static StartupLocations ForCurrentSystem() => new(
        [
            new(StartupSource.RunUser, new(RegistryHive.CurrentUser, $@"Software\{Run}"), new(RegistryHive.CurrentUser, $@"Software\{Approved}\Run")),
            new(StartupSource.RunMachine, new(RegistryHive.LocalMachine, $@"SOFTWARE\{Run}"), new(RegistryHive.LocalMachine, $@"SOFTWARE\{Approved}\Run")),
            new(StartupSource.RunMachine32, new(RegistryHive.LocalMachine, $@"SOFTWARE\WOW6432Node\{Run}"), new(RegistryHive.LocalMachine, $@"SOFTWARE\{Approved}\Run32")),
        ],
        [
            new(StartupSource.StartupFolderUser, Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                new(RegistryHive.CurrentUser, $@"Software\{Approved}\StartupFolder")),
            new(StartupSource.StartupFolderCommon, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
                new(RegistryHive.LocalMachine, $@"SOFTWARE\{Approved}\StartupFolder")),
        ],
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks"));
}

/// <summary>
/// Program awal: Run (HKCU, HKLM 64/32), folder Startup, dan tugas terjadwal saat masuk/mulai.
/// Menonaktifkan memakai StartupApproved (sama dengan Task Manager) atau schtasks /DISABLE untuk tugas;
/// entri aslinya tidak pernah dihapus, jadi selalu bisa diaktifkan lagi.
/// </summary>
public sealed class StartupRepository(StartupLocations locations, IClock clock, ILogger<StartupRepository> logger) : IStartupRepository
{
    public StartupRepository(IClock clock, ILogger<StartupRepository> logger)
        : this(StartupLocations.ForCurrentSystem(), clock, logger)
    {
    }

    public IReadOnlyList<StartupEntry> GetAll()
    {
        var entries = new List<StartupEntry>();
        foreach (var run in locations.RunKeys)
        {
            Safe("Run", () => ReadRun(run, entries));
        }

        foreach (var folder in locations.Folders)
        {
            Safe("folder Startup", () => ReadFolder(folder, entries));
        }

        Safe("tugas terjadwal", () => ReadTasks(entries));
        return [.. entries.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public bool SetEnabled(StartupEntry entry, bool enabled)
    {
        try
        {
            bool ok = entry.Source == StartupSource.ScheduledTask
                ? SetTaskEnabled(entry.Location, enabled)
                : SetApproved(entry, enabled);
            logger.LogInformation("Program awal {Name} ({Source}) {State}: {Ok}", entry.Name, entry.Source, enabled ? "diaktifkan" : "dinonaktifkan", ok);
            return ok;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            logger.LogWarning(ex, "Gagal mengubah program awal {Name}", entry.Name);
            return false;
        }
    }

    // ------------------------------------------------------------------ baca

    private void ReadRun(RunLocation location, List<StartupEntry> entries)
    {
        using var key = Open(location.Run, writable: false);
        if (key is null)
        {
            return;
        }

        using var approved = Open(location.Approved, writable: false);
        foreach (string name in key.GetValueNames().Where(n => n.Length > 0))
        {
            if (key.GetValue(name) is not string command || string.IsNullOrWhiteSpace(command))
            {
                continue;
            }

            entries.Add(new StartupEntry
            {
                Name = name,
                Command = command,
                Source = location.Source,
                Location = location.Run.FullPath,
                ApprovalName = name,
                IsEnabled = StartupApprovedState.IsEnabled(approved?.GetValue(name) as byte[]),
                ExecutablePath = ExecutableOf(command),
            });
        }
    }

    private void ReadFolder(FolderLocation location, List<StartupEntry> entries)
    {
        if (string.IsNullOrWhiteSpace(location.Folder) || !Directory.Exists(location.Folder))
        {
            return;
        }

        using var approved = Open(location.Approved, writable: false);
        foreach (string file in Directory.EnumerateFiles(location.Folder))
        {
            string fileName = Path.GetFileName(file);
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? target = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutResolver.GetTarget(file) : file;
            entries.Add(new StartupEntry
            {
                Name = Path.GetFileNameWithoutExtension(file),
                Command = target ?? file,
                Source = location.Source,
                Location = file,
                ApprovalName = fileName,
                IsEnabled = StartupApprovedState.IsEnabled(approved?.GetValue(fileName) as byte[]),
                ExecutablePath = target,
            });
        }
    }

    private void ReadTasks(List<StartupEntry> entries)
    {
        if (locations.TasksFolder is not { } folder || !Directory.Exists(folder))
        {
            return;
        }

        var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true };
        foreach (string file in Directory.EnumerateFiles(folder, "*", options))
        {
            string taskPath = @"\" + Path.GetRelativePath(folder, file);
            if (ProtectedServices.IsProtectedTask(taskPath) || ReadTask(file) is not { } task)
            {
                continue;
            }

            entries.Add(new StartupEntry
            {
                Name = Path.GetFileName(file),
                Command = task.Command,
                Source = StartupSource.ScheduledTask,
                Location = taskPath,
                IsEnabled = task.Enabled,
                ExecutablePath = ExecutableOf(task.Command),
            });
        }
    }

    /// <summary>Tugas dengan pemicu LogonTrigger/BootTrigger dan aksi Exec; null untuk tugas lain.</summary>
    internal static (string Command, bool Enabled)? ReadTask(string file)
    {
        try
        {
            var doc = XDocument.Load(file);
            var all = doc.Descendants().ToList();
            bool atStartup = all.Any(e => e.Name.LocalName is "LogonTrigger" or "BootTrigger");
            string? command = all.FirstOrDefault(e => e.Name.LocalName == "Command")?.Value.Trim();
            if (!atStartup || string.IsNullOrWhiteSpace(command))
            {
                return null;
            }

            string? arguments = all.FirstOrDefault(e => e.Name.LocalName == "Arguments")?.Value.Trim();
            // <Settings><Enabled>false</Enabled>; elemen Enabled lain (di pemicu) tidak menentukan status tugas.
            var settingsEnabled = all.FirstOrDefault(e => e.Name.LocalName == "Settings")?
                .Elements().FirstOrDefault(e => e.Name.LocalName == "Enabled")?.Value;
            bool enabled = !string.Equals(settingsEnabled?.Trim(), "false", StringComparison.OrdinalIgnoreCase);

            string full = command.Contains(' ') && !command.StartsWith('"') ? $"\"{command}\"" : command;
            return (string.IsNullOrWhiteSpace(arguments) ? full : $"{full} {arguments}", enabled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }

    private static string? ExecutableOf(string command)
    {
        string expanded = Environment.ExpandEnvironmentVariables(command);
        return IconLocation.ExecutableFromCommandLine(expanded)
               ?? (expanded.Trim().Trim('"') is var plain && File.Exists(plain) ? plain : null);
    }

    // ------------------------------------------------------------------ ubah

    private bool SetApproved(StartupEntry entry, bool enabled)
    {
        var location = (RegistryRoot?)locations.RunKeys.FirstOrDefault(r => r.Source == entry.Source)?.Approved
                       ?? locations.Folders.FirstOrDefault(f => f.Source == entry.Source)?.Approved;
        if (location is null || entry.ApprovalName is null)
        {
            return false;
        }

        using var key = Open(location, writable: true, create: true);
        if (key is null)
        {
            return false;
        }

        key.SetValue(entry.ApprovalName, StartupApprovedState.Create(enabled, clock.Now), RegistryValueKind.Binary);
        return true;
    }

    private bool SetTaskEnabled(string taskPath, bool enabled)
    {
        if (ProtectedServices.IsProtectedTask(taskPath))
        {
            return false;
        }

        string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");
        using var process = Process.Start(new ProcessStartInfo(exe, $"/change /tn \"{taskPath}\" {(enabled ? "/ENABLE" : "/DISABLE")}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            process.Kill();
            return false;
        }

        return process.ExitCode == 0;
    }

    private static RegistryKey? Open(RegistryRoot root, bool writable, bool create = false)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(root.Hive, RegistryView.Registry64);
            return create ? baseKey.CreateSubKey(root.SubKey, writable) : baseKey.OpenSubKey(root.SubKey, writable);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private void Safe(string source, Action read)
    {
        try
        {
            read();
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            logger.LogWarning(ex, "Sumber program awal '{Source}' dilewati", source);
        }
    }
}

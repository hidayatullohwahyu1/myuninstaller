namespace MyUninstaller.Core.Safety;

/// <summary>Proses yang sedang berjalan (ringkas, tanpa objek Process).</summary>
public sealed record RunningProcess(int ProcessId, string Name, string? ExecutablePath);

/// <summary>
/// Aturan proses: proses kritis Windows tidak pernah dihentikan, dan hanya proses yang exe-nya berada
/// di dalam folder instalasi program yang ditawarkan untuk ditutup.
/// </summary>
public static class RunningProcessRules
{
    /// <summary>Proses kritis (PRD): tidak pernah dihentikan.</summary>
    public static readonly IReadOnlySet<string> CriticalProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "csrss", "wininit", "winlogon", "lsass", "services", "svchost", "smss", "System", "Idle", "Registry",
        "dwm", "fontdrvhost", "LsaIso", "Memory Compression", "explorer",
    };

    public static bool IsCritical(RunningProcess process) =>
        CriticalProcessNames.Contains(Path.GetFileNameWithoutExtension(process.Name));

    /// <summary>
    /// Proses milik program: exe berada di dalam <paramref name="installLocation"/>.
    /// Proses kritis dan proses aplikasi ini sendiri selalu dikecualikan.
    /// </summary>
    public static IReadOnlyList<RunningProcess> FindProgramProcesses(
        IEnumerable<RunningProcess> processes,
        string? installLocation,
        int ownProcessId)
    {
        if (string.IsNullOrWhiteSpace(installLocation) || !Path.IsPathFullyQualified(installLocation))
        {
            return [];
        }

        string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installLocation)) + Path.DirectorySeparatorChar;

        // Folder sependek "C:\" tidak pernah dianggap folder program.
        if (folder.Length <= 4)
        {
            return [];
        }

        return
        [
            .. processes.Where(p =>
                p.ProcessId != ownProcessId
                && !IsCritical(p)
                && p.ExecutablePath is { Length: > 0 } exe
                && exe.StartsWith(folder, StringComparison.OrdinalIgnoreCase)),
        ];
    }
}

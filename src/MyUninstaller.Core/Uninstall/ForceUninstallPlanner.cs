using MyUninstaller.Core.Models;
using MyUninstaller.Core.Safety;
using MyUninstaller.Core.Scanning;

namespace MyUninstaller.Core.Uninstall;

/// <summary>
/// Hapus paksa (tanpa uninstaller bawaan): folder instalasi + kunci Uninstall programnya.
/// Sisa lain (AppData, layanan, dll.) ditangani pemindai sisa (Tahap 5).
/// </summary>
public static class ForceUninstallPlanner
{
    public static IReadOnlyList<RemovalTarget> Plan(
        InstalledProgram program,
        InstallLocationRules locationRules,
        ProtectedPaths protectedPaths,
        Func<string, bool> directoryExists)
    {
        var targets = new List<RemovalTarget>();

        if (program.Source != ProgramSource.Store
            && locationRules.IsMeasurable(program.InstallLocation)
            && !protectedPaths.IsProtected(program.InstallLocation)
            && directoryExists(program.InstallLocation!))
        {
            targets.Add(new RemovalTarget(RemovalTargetKind.Path, program.InstallLocation!));
        }

        if (program.RegistryKeyPath is { } key && !ProtectedRegistryKeys.IsProtected(key))
        {
            targets.Add(new RemovalTarget(RemovalTargetKind.RegistryKey, key));
        }

        return targets;
    }

    /// <summary>"Hapus entri rusak": hanya kunci Uninstall (berkas tidak disentuh).</summary>
    public static IReadOnlyList<RemovalTarget> PlanBrokenEntry(InstalledProgram program) =>
        program.RegistryKeyPath is { } key && !ProtectedRegistryKeys.IsProtected(key)
            ? [new RemovalTarget(RemovalTargetKind.RegistryKey, key)]
            : [];
}

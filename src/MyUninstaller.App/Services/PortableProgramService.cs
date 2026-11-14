using System.IO;
using Microsoft.Extensions.Logging;
using MyUninstaller.App.Localization;
using MyUninstaller.Core.Portable;
using MyUninstaller.Core.Safety;
using MyUninstaller.Core.Uninstall;

namespace MyUninstaller.App.Services;

/// <summary>Hasil hapus program portable.</summary>
public sealed record PortableRemovalResult(bool Removed, string Message);

/// <summary>
/// Menghapus program portable (versi 2.0): folder dan pintasannya dipindah ke karantina lewat SafeRemover
/// (daftar lindung dicek ulang, dijurnal, bisa dibatalkan dari Riwayat). Program yang masih berjalan TIDAK
/// dihentikan; pengguna diminta menutupnya dulu.
/// </summary>
public sealed class PortableProgramService(
    IProcessInspector processes,
    SafeRemover remover,
    ISessionJournal journal,
    ILogger<PortableProgramService> logger)
{
    public const string SessionMode = "Portable";

    public PortableRemovalResult Remove(PortableProgram program, Func<PortableProgram, bool> confirm)
    {
        if (!Directory.Exists(program.Folder))
        {
            return new PortableRemovalResult(false, Strings.Format("Folder_NotFound", program.Folder));
        }

        string prefix = program.Folder.TrimEnd('\\') + Path.DirectorySeparatorChar;
        var running = processes.GetRunningProcesses()
            .Where(p => p.ExecutablePath?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true)
            .Select(p => p.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (running.Count > 0)
        {
            return new PortableRemovalResult(false, Strings.Format("Portable_Running", program.Name, string.Join(", ", running)));
        }

        if (!confirm(program))
        {
            return new PortableRemovalResult(false, string.Empty);
        }

        var targets = new List<RemovalTarget> { new(RemovalTargetKind.Path, program.Folder) };
        targets.AddRange(program.Shortcuts.Select(s => new RemovalTarget(RemovalTargetKind.Path, s)));

        string session = journal.StartSession(SessionMode, Strings.Format("Portable_SessionTitle", program.Name));
        var results = remover.Remove(session, targets);
        journal.FinishSession(session, JournalSessionStatus.Completed);

        var folder = results[0];
        logger.LogInformation("Program portable {Folder}: {Status}, {Shortcuts} pintasan", program.Folder, folder.Status, program.Shortcuts.Count);
        bool removed = folder.Status is RemovalStatus.Removed or RemovalStatus.PendingReboot;
        return new PortableRemovalResult(removed, removed
            ? Strings.Format("Portable_Removed", program.Name)
            : Strings.Format("Portable_RemoveFailed", program.Name, folder.Detail ?? Strings.Get("Result_" + folder.Status)));
    }
}

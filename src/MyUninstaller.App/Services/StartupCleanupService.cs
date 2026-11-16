using System.IO;
using Microsoft.Extensions.Logging;
using MyUninstaller.App.Localization;
using MyUninstaller.Core.Safety;
using MyUninstaller.Core.Startup;

namespace MyUninstaller.App.Services;

/// <summary>
/// Membuang program awal yang exe-nya sudah TIDAK ADA (sisa program yang sudah terhapus, misalnya oleh antivirus).
/// Lewat SafeRemover: nilai Run diekspor ke .reg, pintasan dikarantina, tugas diekspor ke XML; semua dijurnal dan bisa
/// dibatalkan dari Riwayat. Entri yang berkasnya masih ada tidak pernah dihapus di sini (cukup dinonaktifkan).
/// </summary>
public sealed class StartupCleanupService(SafeRemover remover, ISessionJournal journal, ILogger<StartupCleanupService> logger)
{
    public const string SessionMode = "Startup";

    public static RemovalTarget? TargetOf(StartupEntry entry) => entry.Source switch
    {
        StartupSource.RunMachine or StartupSource.RunMachine32 or StartupSource.RunUser when entry.ApprovalName is { Length: > 0 } value
            => new RemovalTarget(RemovalTargetKind.RegistryValue, entry.Location, value),
        StartupSource.StartupFolderUser or StartupSource.StartupFolderCommon => new RemovalTarget(RemovalTargetKind.Path, entry.Location),
        StartupSource.ScheduledTask => new RemovalTarget(RemovalTargetKind.ScheduledTask, entry.Location),
        _ => null,
    };

    public bool RemoveMissing(StartupEntry entry)
    {
        // Diperiksa ulang tepat sebelum menghapus: berkas bisa saja sudah kembali.
        if (entry.ExecutablePath is not { } exe || File.Exists(exe) || TargetOf(entry) is not { } target)
        {
            logger.LogWarning("Program awal {Name} tidak dibuang: berkasnya ada atau jenisnya tidak dikenal", entry.Name);
            return false;
        }

        string session = journal.StartSession(SessionMode, Strings.Format("Startup_SessionTitle", entry.Name));
        var result = remover.Remove(session, [target]).Single();
        journal.FinishSession(session, JournalSessionStatus.Completed);
        logger.LogInformation("Program awal {Name} (berkas hilang) dibuang: {Status}", entry.Name, result.Status);
        return result.Status is RemovalStatus.Removed or RemovalStatus.PendingReboot;
    }
}

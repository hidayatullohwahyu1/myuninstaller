namespace MyUninstaller.Core.Safety;

public sealed record RestoreSummary(int FilesRestored, int KeysRestored, int Failed);

/// <summary>
/// Batalkan satu sesi: aksi karantina, registri, layanan, dan tugas dipulihkan dalam urutan terbalik.
/// Dipakai menu Riwayat, tombol Batalkan di ringkasan, dan pemulihan setelah crash.
/// </summary>
public sealed class SessionRestorer(
    ISessionJournal journal,
    IQuarantineService quarantine,
    IRegistryBackupService registry,
    IScheduledTaskRemover? tasks = null)
{
    public RestoreSummary Restore(string sessionId)
    {
        int files = 0, keys = 0, failed = 0;

        foreach (var action in journal.GetActions(sessionId).OrderByDescending(a => a.Id))
        {
            // Pending juga dicoba: aksi bisa setengah jalan saat crash, dan cadangannya mungkin sudah ada.
            if (action.Status is not (JournalActionStatus.Done or JournalActionStatus.Pending) || action.BackupPath is null)
            {
                continue;
            }

            bool ok;
            try
            {
                ok = action.Kind switch
                {
                    JournalActionKind.QuarantinePath => quarantine.Restore(action.BackupPath, action.Target),

                    // Layanan: kunci layanan diimpor ulang; Windows mengenalinya lagi setelah mulai ulang.
                    JournalActionKind.DeleteRegistryKey or JournalActionKind.DeleteRegistryValue or JournalActionKind.DeleteService
                        => registry.Import(action.BackupPath),
                    JournalActionKind.DeleteScheduledTask => tasks?.Import(action.BackupPath, action.Target) ?? false,
                    _ => true,
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                ok = false;
            }

            if (!ok)
            {
                failed++;
                journal.Complete(action.Id, action.Status, "Gagal dipulihkan", action.BackupPath);
                continue;
            }

            journal.Complete(action.Id, JournalActionStatus.Restored, null, action.BackupPath);
            if (action.Kind == JournalActionKind.QuarantinePath)
            {
                files++;
            }
            else if (action.Kind is not (JournalActionKind.RestorePoint or JournalActionKind.RunUninstaller or JournalActionKind.KillProcess))
            {
                keys++;
            }
        }

        journal.FinishSession(sessionId, failed == 0 ? JournalSessionStatus.Restored : JournalSessionStatus.Completed,
            failed == 0 ? null : $"{failed} item gagal dipulihkan");
        return new RestoreSummary(files, keys, failed);
    }
}

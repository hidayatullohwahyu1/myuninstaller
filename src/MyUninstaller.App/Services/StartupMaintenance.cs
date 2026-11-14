using System.Windows;
using Microsoft.Extensions.Logging;
using MyUninstaller.App.Localization;
using MyUninstaller.App.Views;
using MyUninstaller.Core.Abstractions;
using MyUninstaller.Core.Formatting;
using MyUninstaller.Core.Safety;

namespace MyUninstaller.App.Services;

/// <summary>
/// Tugas saat aplikasi dibuka: tawarkan pemulihan sesi yang terputus (crash/listrik padam) dan
/// kosongkan karantina yang melewati masa simpan.
/// </summary>
public sealed class StartupMaintenance(
    ISessionJournal journal,
    SessionRestorer restorer,
    IQuarantineService quarantine,
    ISettingsStore settings,
    ILogger<StartupMaintenance> logger)
{
    public void OfferRecovery(Window owner)
    {
        IReadOnlyList<JournalSession> unfinished;
        try
        {
            unfinished = journal.GetUnfinishedSessions();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogError(ex, "Jurnal sesi tidak bisa dibaca");
            return;
        }

        foreach (var session in unfinished)
        {
            string when = IndonesianFormat.DateTime(session.StartedAt.LocalDateTime);
            var actions = journal.GetActions(session.Id)
                .Where(a => a.Kind is JournalActionKind.QuarantinePath or JournalActionKind.DeleteRegistryKey
                            && a.Status is JournalActionStatus.Done or JournalActionStatus.Pending)
                .Select(a => $"{a.Status}: {a.Target}")
                .ToList();

            if (actions.Count == 0)
            {
                // Tidak ada yang dipindah/dihapus: cukup tandai terputus.
                journal.FinishSession(session.Id, JournalSessionStatus.Interrupted);
                continue;
            }

            int choice = ChoiceDialog.Show(owner, Strings.Get("Recovery_Title"),
                Strings.Format("Recovery_Message", session.Title, when),
            [
                new ChoiceButton(Strings.Get("Recovery_Keep"), IsCancel: true),
                new ChoiceButton(Strings.Get("Recovery_Restore"), ChoiceKind.Primary, IsDefault: true),
            ],
            actions,
            glyph: "");

            if (choice != 1)
            {
                journal.FinishSession(session.Id, JournalSessionStatus.Interrupted);
                continue;
            }

            var summary = restorer.Restore(session.Id);
            logger.LogInformation("Sesi terputus {Session} dipulihkan: {Summary}", session.Id, summary);
            ChoiceDialog.Show(owner, Strings.Get("Recovery_Title"),
                summary.Failed == 0
                    ? Strings.Format("Recovery_Done", summary.FilesRestored, summary.KeysRestored)
                    : Strings.Format("Recovery_DoneWithFailures", summary.FilesRestored, summary.KeysRestored, summary.Failed),
                [new ChoiceButton(Strings.Get("Common_Close"), ChoiceKind.Primary, IsDefault: true, IsCancel: true)],
                glyph: "");
        }
    }

    /// <summary>Di latar belakang: kosongkan karantina kedaluwarsa (PRD: 30 hari, bisa diatur).</summary>
    public Task PurgeQuarantineAsync() => Task.Run(() =>
    {
        try
        {
            int days = settings.Load().QuarantineRetentionDays;
            int purged = quarantine.PurgeExpired(days);
            if (purged > 0)
            {
                logger.LogInformation("{Count} sesi karantina lebih dari {Days} hari dikosongkan", purged, days);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Pembersihan karantina gagal");
        }
    });
}

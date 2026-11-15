using System.Globalization;
using System.Text;
using MyUninstaller.Core.Formatting;

namespace MyUninstaller.Core.Safety;

/// <summary>Ringkasan angka satu sesi di menu Riwayat.</summary>
/// <param name="Paths">Berkas/folder yang dipindah ke karantina.</param>
/// <param name="RegistryItems">Kunci/nilai registri yang dihapus (dengan cadangan .reg).</param>
/// <param name="ServicesAndTasks">Layanan dan tugas terjadwal yang dihapus.</param>
/// <param name="Uninstallers">Penghapus instalan program yang dijalankan.</param>
/// <param name="Failed">Aksi yang gagal.</param>
/// <param name="Restored">Aksi yang sudah dipulihkan.</param>
/// <param name="Restorable">Aksi yang masih bisa dipulihkan (cadangannya masih ada).</param>
public sealed record SessionSummary(int Paths, int RegistryItems, int ServicesAndTasks, int Uninstallers, int Failed, int Restored, int Restorable);

/// <summary>Logika menu Riwayat: ringkasan, boleh-tidaknya Batalkan, dan teks "Salin laporan".</summary>
public static class HistoryReport
{
    private static readonly JournalActionKind[] RestorableKinds =
    [
        JournalActionKind.QuarantinePath,
        JournalActionKind.DeleteRegistryKey,
        JournalActionKind.DeleteRegistryValue,
        JournalActionKind.DeleteService,
        JournalActionKind.DeleteScheduledTask,
        JournalActionKind.EditPathVariable,
    ];

    /// <param name="actions">Aksi sesi.</param>
    /// <param name="backupExists">Memeriksa apakah cadangan (berkas karantina/.reg/.xml) masih ada.</param>
    public static SessionSummary Summarize(IReadOnlyList<JournalAction> actions, Func<string, bool> backupExists)
    {
        static bool Done(JournalAction a) => a.Status is JournalActionStatus.Done or JournalActionStatus.Restored;

        return new SessionSummary(
            Paths: actions.Count(a => a.Kind == JournalActionKind.QuarantinePath && Done(a)),
            RegistryItems: actions.Count(a => a.Kind is JournalActionKind.DeleteRegistryKey or JournalActionKind.DeleteRegistryValue or JournalActionKind.EditPathVariable && Done(a)),
            ServicesAndTasks: actions.Count(a => a.Kind is JournalActionKind.DeleteService or JournalActionKind.DeleteScheduledTask && Done(a)),
            Uninstallers: actions.Count(a => a.Kind == JournalActionKind.RunUninstaller),
            Failed: actions.Count(a => a.Status == JournalActionStatus.Failed),
            Restored: actions.Count(a => a.Status == JournalActionStatus.Restored),
            Restorable: actions.Count(a => IsRestorable(a, backupExists)));
    }

    public static bool IsRestorable(JournalAction action, Func<string, bool> backupExists) =>
        RestorableKinds.Contains(action.Kind)
        && action.Status is JournalActionStatus.Done or JournalActionStatus.Pending
        && action.BackupPath is { Length: > 0 } backup
        && backupExists(backup);

    /// <summary>Batalkan hanya untuk sesi yang sudah berhenti dan masih punya sesuatu untuk dipulihkan.</summary>
    public static bool CanUndo(JournalSession session, SessionSummary summary) =>
        session.Status != JournalSessionStatus.Running && summary.Restorable > 0;

    /// <summary>Teks laporan untuk papan klip (tombol "Salin laporan"), dalam bahasa Indonesia.</summary>
    public static string BuildText(
        JournalSession session,
        IReadOnlyList<JournalAction> actions,
        string appVersion,
        string logFolder,
        Func<JournalSessionStatus, string> statusText,
        Func<JournalActionKind, string> kindText,
        Func<JournalActionStatus, string> actionStatusText)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Laporan My Uninstaller! {appVersion}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Windows {Environment.OSVersion.Version}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Sesi: {session.Id}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Judul: {session.Title}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Mode: {session.Mode}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Mulai: {IndonesianFormat.DateTime(session.StartedAt.LocalDateTime)}");
        if (session.FinishedAt is { } finished)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Selesai: {IndonesianFormat.DateTime(finished.LocalDateTime)}");
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"Status: {statusText(session.Status)}");
        if (!string.IsNullOrWhiteSpace(session.Note))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Catatan: {session.Note}");
        }

        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Aksi ({actions.Count}):");
        foreach (var action in actions)
        {
            text.Append(CultureInfo.InvariantCulture, $"- [{actionStatusText(action.Status)}] {kindText(action.Kind)}: {action.Target}");
            if (!string.IsNullOrWhiteSpace(action.Detail))
            {
                text.Append(CultureInfo.InvariantCulture, $" ({action.Detail})");
            }

            text.AppendLine();
            if (action.BackupPath is not null)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"    cadangan: {action.BackupPath}");
            }
        }

        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Catatan aktivitas lengkap: {logFolder}");
        return text.ToString();
    }
}

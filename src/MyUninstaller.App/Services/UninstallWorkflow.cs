using System.IO;
using System.Windows;
using Microsoft.Extensions.Logging;
using MyUninstaller.App.Localization;
using MyUninstaller.App.ViewModels;
using MyUninstaller.App.Views;
using MyUninstaller.Core.Abstractions;
using MyUninstaller.Core.Leftovers;
using MyUninstaller.Core.Models;
using MyUninstaller.Core.Safety;
using MyUninstaller.Core.Scanning;
using MyUninstaller.Core.Uninstall;

namespace MyUninstaller.App.Services;

/// <summary>
/// Alur hapus instalan di UI: dialog mode → (Hapus paksa: pratinjau) → jendela proses.
/// </summary>
public sealed class UninstallWorkflow(
    UninstallRunner runner,
    IProcessInspector processes,
    ISettingsStore settingsStore,
    InstallLocationRules locationRules,
    ProtectedPaths protectedPaths,
    ISessionJournal journal,
    SafeRemover remover,
    IRestorePointService restorePoints,
    ILeftoverScanner leftoverScanner,
    SessionRestorer restorer,
    ILogger<UninstallWorkflow> logger)
{
    /// <summary>
    /// Menjalankan alur untuk program terpilih. Mengembalikan true bila ada program yang dihapus
    /// (daftar perlu disegarkan).
    /// </summary>
    /// <param name="programs">Program yang akan dihapus.</param>
    /// <param name="allPrograms">Semua program terpasang (untuk melindungi milik program lain saat mencari sisa).</param>
    /// <param name="forceMode">Langsung Hapus paksa tanpa dialog mode.</param>
    public bool Start(IReadOnlyList<ProgramItemViewModel> programs, IReadOnlyList<InstalledProgram> allPrograms, bool forceMode = false)
    {
        if (programs.Count == 0)
        {
            return false;
        }

        Window? owner = Application.Current?.MainWindow;
        UninstallMode mode;

        if (forceMode)
        {
            mode = UninstallMode.Force;
        }
        else
        {
            string header = programs.Count == 1
                ? Strings.Format("Mode_HeaderSingle", programs[0].DisplayName)
                : Strings.Format("Mode_HeaderMany", programs.Count);

            var dialog = new UninstallModeDialog(header, settingsStore.Load().LastUninstallMode) { Owner = owner };
            if (dialog.ShowDialog() != true)
            {
                return false;
            }

            mode = dialog.SelectedMode;
            if (mode != UninstallMode.Force)
            {
                SaveLastMode(mode);
            }
        }

        // Hapus paksa: konfirmasi kedua dengan daftar target (PRD: penghapusan di luar alur uninstaller).
        if (mode == UninstallMode.Force && !ConfirmForce(owner, programs))
        {
            return false;
        }

        logger.LogInformation("Sesi hapus instalan dimulai: {Count} program, mode {Mode}", programs.Count, mode);

        UninstallProgressWindow? window = null;
        var services = new UninstallSessionServices(
            runner,
            processes,
            journal,
            remover,
            PlanForce,
            settingsStore.Load().CreateRestorePoint ? restorePoints.CreateAsync : null,
            leftoverScanner,
            allPrograms);

        var session = new UninstallSessionViewModel(
            programs,
            mode,
            services,
            askRunning: (name, running) => AskRunning(window, name, running),
            confirmStop: () => ConfirmStop(window),
            logger);

        window = new UninstallProgressWindow(session) { Owner = owner };
        window.ShowDialog();

        if (session.ReviewRequested && session.Leftovers.Count > 0)
        {
            ShowLeftovers(owner, session.Leftovers, session.SessionId);
        }

        return session.AnyRemoved;
    }

    /// <summary>Layar hasil sisa. Semua item dari satu sesi (juga uninstall massal) ada di satu layar.</summary>
    private void ShowLeftovers(Window? owner, IReadOnlyList<LeftoverItem> items, string sessionId)
    {
        LeftoverResultsWindow? window = null;
        var viewModel = new LeftoverResultsViewModel(
            items,
            sessionId,
            remover,
            restorer,
            journal,
            confirmRisky: count => ChoiceDialog.Show(window, Strings.Get("Leftovers_ConfirmRiskyTitle"),
                Strings.Format("Leftovers_ConfirmRisky", count),
            [
                new ChoiceButton(Strings.Get("Common_Cancel"), ChoiceKind.Primary, IsDefault: true, IsCancel: true),
                new ChoiceButton(Strings.Get("Leftovers_ConfirmRiskyYes"), ChoiceKind.Danger),
            ],
            glyph: "") == 1,
            confirmUndo: () => ChoiceDialog.Show(window, Strings.Get("Leftovers_Undo"), Strings.Get("Leftovers_UndoConfirm"),
            [
                new ChoiceButton(Strings.Get("Common_Cancel"), IsCancel: true),
                new ChoiceButton(Strings.Get("Leftovers_Undo"), ChoiceKind.Primary, IsDefault: true),
            ],
            glyph: "") == 1,
            logger);

        window = new LeftoverResultsWindow(viewModel) { Owner = owner };
        window.ShowDialog();
    }

    /// <summary>Entri rusak: tidak ada perintah hapus instalan, atau berkas uninstaller-nya hilang.</summary>
    public bool IsBroken(InstalledProgram program) =>
        runner.PlanCommand(program, silent: false) is not { } command
        || (command.Kind == UninstallCommandKind.Executable && !File.Exists(command.FileName));

    private IReadOnlyList<RemovalTarget> PlanForce(InstalledProgram program) =>
        ForceUninstallPlanner.Plan(program, locationRules, protectedPaths, Directory.Exists);

    /// <summary>
    /// "Hapus entri rusak": hanya kunci Uninstall (dicadangkan ke .reg), berkas tidak disentuh.
    /// Mengembalikan true bila entri terhapus.
    /// </summary>
    public bool RemoveBrokenEntry(InstalledProgram program)
    {
        Window? owner = Application.Current?.MainWindow;
        var targets = ForceUninstallPlanner.PlanBrokenEntry(program);
        if (targets.Count == 0)
        {
            ChoiceDialog.Show(owner, Strings.Get("Broken_Title"), Strings.Get("Force_NoTargets"),
                [new ChoiceButton(Strings.Get("Common_Close"), ChoiceKind.Primary, IsDefault: true, IsCancel: true)]);
            return false;
        }

        int choice = ChoiceDialog.Show(owner, Strings.Get("Broken_Title"), Strings.Format("Broken_Message", program.DisplayName),
        [
            new ChoiceButton(Strings.Get("Common_Cancel"), ChoiceKind.Primary, IsDefault: true, IsCancel: true),
            new ChoiceButton(Strings.Get("Broken_Confirm"), ChoiceKind.Danger),
        ],
        [.. targets.Select(t => Strings.Format("Force_TargetRegistry", t.Location))],
        glyph: "\uE7BA");
        if (choice != 1)
        {
            return false;
        }

        string session = journal.StartSession("BrokenEntry", program.DisplayName);
        var result = remover.Remove(session, targets).Single();
        journal.FinishSession(session, JournalSessionStatus.Completed);
        logger.LogInformation("Hapus entri rusak {Program}: {Status}", program.DisplayName, result.Status);

        bool ok = result.Status is RemovalStatus.Removed or RemovalStatus.Missing;
        ChoiceDialog.Show(owner, Strings.Get("Broken_Title"),
            ok ? Strings.Get("Broken_Done") : Strings.Format("Broken_Failed", result.Detail ?? result.Status.ToString()),
            [new ChoiceButton(Strings.Get("Common_Close"), ChoiceKind.Primary, IsDefault: true, IsCancel: true)],
            glyph: ok ? "\uE73E" : "\uE7BA");
        return ok;
    }

    /// <summary>Konfirmasi kedua Hapus paksa: tampilkan semua target. Tombol merah, tidak pernah tombol Enter.</summary>
    private bool ConfirmForce(Window? owner, IReadOnlyList<ProgramItemViewModel> programs)
    {
        var targets = new List<string>();
        foreach (var program in programs.Select(p => p.Model))
        {
            if (programs.Count > 1)
            {
                targets.Add($"• {program.DisplayName}");
            }

            if (program.Source == ProgramSource.Store)
            {
                targets.Add("  " + Strings.Format("Force_TargetStore", program.Id.Replace("Store:", string.Empty, StringComparison.Ordinal)));
                continue;
            }

            foreach (var target in PlanForce(program))
            {
                targets.Add("  " + Strings.Format(
                    target.Kind == RemovalTargetKind.Path ? "Force_TargetFolder" : "Force_TargetRegistry", target.Location));
            }
        }

        if (targets.All(t => t.StartsWith('•')))
        {
            ChoiceDialog.Show(owner, Strings.Get("Force_Title"), Strings.Get("Force_NoTargets"),
                [new ChoiceButton(Strings.Get("Common_Close"), ChoiceKind.Primary, IsDefault: true, IsCancel: true)]);
            return false;
        }

        return ChoiceDialog.Show(owner, Strings.Get("Force_Title"), Strings.Get("Force_Message"),
        [
            new ChoiceButton(Strings.Get("Common_Cancel"), ChoiceKind.Primary, IsDefault: true, IsCancel: true),
            new ChoiceButton(Strings.Get("Force_Confirm"), ChoiceKind.Danger),
        ],
        targets,
        glyph: "\uE945") == 1;
    }

    private static RunningProcessChoice AskRunning(Window? owner, string programName, IReadOnlyList<RunningProcess> running)
    {
        var details = running.Select(p => $"{p.Name}  (PID {p.ProcessId})  {p.ExecutablePath}").ToList();
        int choice = ChoiceDialog.Show(owner, Strings.Get("Running_Title"), Strings.Format("Running_Message", programName),
        [
            new ChoiceButton(Strings.Get("Running_Skip"), IsCancel: true),
            new ChoiceButton(Strings.Get("Running_Ignore")),
            new ChoiceButton(Strings.Get("Running_Kill"), ChoiceKind.Danger),
        ],
        details,
        glyph: "");

        return choice switch
        {
            1 => RunningProcessChoice.Ignore,
            2 => RunningProcessChoice.Kill,
            _ => RunningProcessChoice.Skip,
        };
    }

    private static bool ConfirmStop(Window? owner) =>
        ChoiceDialog.Show(owner, Strings.Get("Progress_Stop"), Strings.Get("Progress_StopConfirm"),
        [
            new ChoiceButton(Strings.Get("Common_Cancel"), ChoiceKind.Primary, IsDefault: true, IsCancel: true),
            new ChoiceButton(Strings.Get("Progress_Stop"), ChoiceKind.Danger),
        ],
        glyph: "") == 1;

    private void SaveLastMode(UninstallMode mode)
    {
        try
        {
            settingsStore.Save(settingsStore.Load() with { LastUninstallMode = mode });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Gagal menyimpan mode terakhir");
        }
    }
}

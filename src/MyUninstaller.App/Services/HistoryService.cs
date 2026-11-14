using System.Windows;
using Microsoft.Extensions.Logging;
using MyUninstaller.App.Localization;
using MyUninstaller.App.ViewModels;
using MyUninstaller.App.Views;
using MyUninstaller.Core.Abstractions;
using MyUninstaller.Core.Safety;
using MyUninstaller.Infrastructure;

namespace MyUninstaller.App.Services;

/// <summary>Membuka menu Riwayat dengan dialog konfirmasinya.</summary>
public sealed class HistoryService(
    ISessionJournal journal,
    SessionRestorer restorer,
    IQuarantineService quarantine,
    IFolderSizeCalculator sizes,
    IShellService shell,
    ISettingsStore settings,
    AppPaths paths,
    ILogger<HistoryService> logger)
{
    /// <summary>Menampilkan Riwayat. Mengembalikan true bila ada sesi yang dibatalkan (daftar program perlu disegarkan).</summary>
    public bool Show()
    {
        Window? owner = Application.Current?.MainWindow;
        HistoryWindow? window = null;

        var dialogs = new HistoryDialogs(
            ConfirmUndo: title => ChoiceDialog.Show(window, Strings.Get("History_Undo"), Strings.Format("History_UndoConfirm", title),
            [
                new ChoiceButton(Strings.Get("Common_Cancel"), IsCancel: true),
                new ChoiceButton(Strings.Get("History_Undo"), ChoiceKind.Primary, IsDefault: true),
            ],
            glyph: "") == 1,
            ConfirmPurge: (title, size) => ChoiceDialog.Show(window, Strings.Get("History_Purge"), Strings.Format("History_PurgeConfirm", title, size),
            [
                new ChoiceButton(Strings.Get("Common_Cancel"), ChoiceKind.Primary, IsDefault: true, IsCancel: true),
                new ChoiceButton(Strings.Get("History_PurgeConfirmYes"), ChoiceKind.Danger),
            ],
            glyph: "") == 1);

        var viewModel = new HistoryViewModel(journal, restorer, quarantine, sizes, shell, settings, dialogs, paths.LogsDirectory, logger);
        window = new HistoryWindow(viewModel) { Owner = owner };
        window.ShowDialog();
        return viewModel.AnyRestored;
    }
}

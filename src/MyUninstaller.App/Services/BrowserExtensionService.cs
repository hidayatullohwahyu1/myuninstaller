using Microsoft.Extensions.Logging;
using MyUninstaller.App.Localization;
using MyUninstaller.Core.Browsers;
using MyUninstaller.Core.Safety;
using MyUninstaller.Core.Uninstall;
using MyUninstaller.Infrastructure.Processes;

namespace MyUninstaller.App.Services;

/// <summary>Hasil satu permintaan hapus ekstensi.</summary>
/// <param name="Removed">True bila berkas ekstensi benar-benar dipindah (Firefox).</param>
/// <param name="Message">Pesan untuk pengguna.</param>
public sealed record ExtensionRemovalResult(bool Removed, string Message);

/// <summary>
/// Menghapus ekstensi browser (versi 1.3).
/// Chromium: halaman ekstensi dibuka di browser (profil yang benar) sebagai pengguna biasa, pengguna menekan "Hapus"
/// di sana. Menghapus folder langsung tidak dipakai: Chrome menganggapnya rusak lalu memasang ulang, dan
/// Secure Preferences dilindungi tanda tangan.
/// Firefox: berkas .xpi dipindah ke karantina (SafeRemover, dijurnal, bisa dipulihkan) saat Firefox tertutup.
/// </summary>
public sealed class BrowserExtensionService(
    IUnelevatedLauncher launcher,
    IProcessInspector processes,
    SafeRemover remover,
    ISessionJournal journal,
    IShellService shell,
    ILogger<BrowserExtensionService> logger)
{
    public static string BrowserName(BrowserKind browser) => browser switch
    {
        BrowserKind.Chrome => "Google Chrome",
        BrowserKind.Edge => "Microsoft Edge",
        BrowserKind.Brave => "Brave",
        _ => "Firefox",
    };

    /// <param name="extension">Ekstensi yang dipilih.</param>
    /// <param name="confirmFirefox">Konfirmasi pemindahan ke karantina (judul ekstensi, profil) → setuju?</param>
    public ExtensionRemovalResult Remove(BrowserExtension extension, Func<string, string, bool> confirmFirefox)
    {
        return extension.Browser == BrowserKind.Firefox
            ? RemoveFirefox(extension, confirmFirefox)
            : OpenInChromium(extension);
    }

    private ExtensionRemovalResult OpenInChromium(BrowserExtension extension)
    {
        string browserName = BrowserName(extension.Browser);
        string? exe = launcher.FindBrowser(extension.Browser);
        if (exe is null)
        {
            shell.CopyText(extension.Id);
            return new ExtensionRemovalResult(false, Strings.Format("Extensions_BrowserNotFound", browserName));
        }

        string scheme = extension.Browser switch
        {
            BrowserKind.Edge => "edge",
            BrowserKind.Brave => "brave",
            _ => "chrome",
        };

        // ID ekstensi Chromium hanya huruf a–p (sudah divalidasi pembaca); folder profil diapit kutip.
        string arguments = $"--profile-directory=\"{extension.ProfileFolder}\" {scheme}://extensions/?id={extension.Id}";
        if (!launcher.Launch(exe, arguments))
        {
            shell.CopyText(extension.Id);
            return new ExtensionRemovalResult(false, Strings.Get("Extensions_LaunchFailed"));
        }

        logger.LogInformation("Halaman ekstensi {Id} dibuka di {Browser} ({Profile})", extension.Id, extension.Browser, extension.ProfileFolder);
        return new ExtensionRemovalResult(false, Strings.Format("Extensions_OpenedInBrowser", extension.Name, browserName));
    }

    private ExtensionRemovalResult RemoveFirefox(BrowserExtension extension, Func<string, string, bool> confirm)
    {
        if (processes.GetRunningProcesses().Any(p => p.Name.Equals("firefox", StringComparison.OrdinalIgnoreCase)))
        {
            return new ExtensionRemovalResult(false, Strings.Get("Extensions_FirefoxRunning"));
        }

        if (!confirm(extension.Name, extension.ProfileName))
        {
            return new ExtensionRemovalResult(false, string.Empty);
        }

        string session = journal.StartSession("Extension", Strings.Format("Extensions_SessionTitle", extension.Name));
        var result = remover.Remove(session, [new RemovalTarget(RemovalTargetKind.Path, extension.Path)]).Single();
        journal.FinishSession(session, JournalSessionStatus.Completed);
        logger.LogInformation("Ekstensi Firefox {Id}: {Status}", extension.Id, result.Status);

        bool removed = result.Status is RemovalStatus.Removed or RemovalStatus.PendingReboot;
        return new ExtensionRemovalResult(removed, removed
            ? Strings.Format("Extensions_Removed", extension.Name)
            : Strings.Format("Extensions_RemoveFailed", extension.Name, result.Detail ?? Strings.Get("Result_" + result.Status)));
    }
}

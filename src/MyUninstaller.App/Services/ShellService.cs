using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Extensions.Logging;

namespace MyUninstaller.App.Services;

/// <summary>Membuka folder, tautan, dan papan klip.</summary>
public interface IShellService
{
    /// <summary>Membuka folder di File Explorer. False bila folder tidak ada.</summary>
    bool OpenFolder(string path);

    /// <summary>Membuka tautan http/https di peramban bawaan. Skema lain ditolak.</summary>
    bool OpenUrl(string url);

    void CopyText(string text);

    /// <summary>Membuka Editor Registri tepat di kunci tertentu (HKEY_...\...).</summary>
    bool OpenRegistryEditor(string keyPath);
}

public sealed class ShellService(ILogger<ShellService> logger) : IShellService
{
    public bool OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return false;
        }

        return StartExplorer($"\"{Path.GetFullPath(path)}\"");
    }

    public bool OpenUrl(string url)
    {
        // Nilai dari registri tidak tepercaya: hanya izinkan http/https agar tidak menjalankan berkas.
        if (!IsSafeWebUrl(url, out Uri? uri))
        {
            logger.LogWarning("Tautan ditolak karena bukan http/https: {Url}", url);
            return false;
        }

        // Lewat explorer.exe agar peramban tidak ikut berjalan dengan hak Administrator.
        return StartExplorer($"\"{uri!.AbsoluteUri}\"");
    }

    public void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            // Papan klip sedang dikunci aplikasi lain.
            logger.LogWarning(ex, "Gagal menyalin ke papan klip");
        }
    }

    public bool OpenRegistryEditor(string keyPath)
    {
        if (string.IsNullOrWhiteSpace(keyPath) || !keyPath.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            // Regedit membuka kunci terakhir dari nilai LastKey. Awalan akar ("Computer\") bergantung bahasa
            // Windows, jadi ambil dari nilai lama bila ada.
            using var applet = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RegeditAppletKey, writable: true);
            string prefix = "Computer";
            if (applet.GetValue("LastKey") is string last && last.IndexOf(@"\HKEY_", StringComparison.OrdinalIgnoreCase) is > 0 and var at)
            {
                prefix = last[..at];
            }

            applet.SetValue("LastKey", $@"{prefix}\{keyPath}", Microsoft.Win32.RegistryValueKind.String);

            string regedit = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "regedit.exe");
            // /m: jendela baru walau regedit sudah terbuka, supaya langsung ke kunci yang diminta.
            using var _ = Process.Start(new ProcessStartInfo(regedit, "/m") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or UnauthorizedAccessException
                                       or System.Security.SecurityException or IOException)
        {
            logger.LogWarning(ex, "Gagal membuka Editor Registri di {Key}", keyPath);
            return false;
        }
    }

    private const string RegeditAppletKey = @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit";

    internal static bool IsSafeWebUrl(string? url, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        string candidate = url.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = "https://" + candidate;
        }

        return Uri.TryCreate(candidate, UriKind.Absolute, out uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private bool StartExplorer(string arguments)
    {
        try
        {
            string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            using var _ = Process.Start(new ProcessStartInfo(explorer, arguments) { UseShellExecute = false });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "Gagal menjalankan explorer.exe {Arguments}", arguments);
            return false;
        }
    }
}

using Microsoft.Win32;

namespace MyUninstaller.Infrastructure.Theming;

/// <summary>Tema Windows saat ini.</summary>
/// <param name="IsLight">True bila aplikasi diminta memakai tema terang.</param>
/// <param name="AccentArgb">Warna aksen dalam format 0xAARRGGBB, atau null bila tidak terbaca.</param>
public sealed record WindowsTheme(bool IsLight, uint? AccentArgb);

/// <summary>Membaca tema terang/gelap dan warna aksen dari registri pengguna.</summary>
public interface IWindowsThemeReader
{
    WindowsTheme Read();
}

public sealed class WindowsThemeReader : IWindowsThemeReader
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string DwmKey = @"Software\Microsoft\Windows\DWM";

    public WindowsTheme Read()
    {
        bool isLight = true;
        uint? accent = null;

        try
        {
            using var personalize = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            if (personalize?.GetValue("AppsUseLightTheme") is int light)
            {
                isLight = light != 0;
            }

            using var dwm = Registry.CurrentUser.OpenSubKey(DwmKey);
            if (dwm?.GetValue("AccentColor") is int abgr)
            {
                accent = AbgrToArgb(unchecked((uint)abgr));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Tetap pakai nilai bawaan.
        }

        return new WindowsTheme(isLight, accent);
    }

    /// <summary>Registri DWM menyimpan warna sebagai 0xAABBGGRR.</summary>
    internal static uint AbgrToArgb(uint abgr)
    {
        uint r = abgr & 0xFF;
        uint g = (abgr >> 8) & 0xFF;
        uint b = (abgr >> 16) & 0xFF;
        return 0xFF000000 | (r << 16) | (g << 8) | b;
    }
}

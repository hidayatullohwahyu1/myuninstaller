using System.Globalization;
using System.Resources;

namespace MyUninstaller.App.Localization;

/// <summary>Akses teks antarmuka dari Resources/Strings.id.resx.</summary>
public static class Strings
{
    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("id-ID");

    private static readonly ResourceManager Manager =
        new("MyUninstaller.App.Resources.Strings", typeof(Strings).Assembly);

    /// <summary>Teks untuk kunci; kunci yang hilang tampil sebagai [[kunci]] agar mudah terlihat.</summary>
    public static string Get(string key) => Manager.GetString(key, CultureInfo.InvariantCulture) ?? $"[[{key}]]";

    /// <summary>Teks berformat, misalnya Format("Info_Installed", "2 September 2026").</summary>
    public static string Format(string key, params object?[] args) => string.Format(Culture, Get(key), args);
}

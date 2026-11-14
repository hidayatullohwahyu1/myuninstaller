using MyUninstaller.Core.Formatting;
using MyUninstaller.Core.Models;

namespace MyUninstaller.Core.Scanning;

/// <summary>
/// Mengubah entri registri mentah menjadi <see cref="InstalledProgram"/> dan menentukan apakah entri
/// disembunyikan bawaan (meniru "Aplikasi terinstal" di Pengaturan Windows).
/// </summary>
public static class UninstallEntryMapper
{
    // Nilai ReleaseType untuk pembaruan Windows/Office yang tidak perlu tampil sebagai program.
    private static readonly HashSet<string> UpdateReleaseTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Security Update", "Update", "Hotfix", "Update Rollup", "Service Pack", "Critical Update",
    };

    public static InstalledProgram Map(RawUninstallEntry entry)
    {
        string? name = Clean(entry.DisplayName);
        Guid? productCode = TryParseProductCode(entry.KeyName);

        return new InstalledProgram
        {
            Id = $"{entry.Source}:{entry.KeyName}",
            DisplayName = name ?? entry.KeyName,
            Source = entry.Source,
            Publisher = Clean(entry.Publisher),
            Version = Clean(entry.DisplayVersion),
            SizeBytes = entry.EstimatedSizeKb is > 0 and var kb ? kb * 1024 : null,
            InstallDate = IndonesianFormat.ParseRegistryDate(entry.InstallDate) ?? entry.KeyLastWriteDate,
            InstallLocation = CleanPath(entry.InstallLocation),
            IconPath = Clean(entry.DisplayIcon),
            UninstallString = Clean(entry.UninstallString),
            QuietUninstallString = Clean(entry.QuietUninstallString),
            HelpLink = Clean(entry.HelpLink),
            UrlInfoAbout = Clean(entry.UrlInfoAbout),
            RegistryKeyPath = entry.RegistryPath,
            IsWindowsInstaller = entry.WindowsInstaller,
            ProductCode = productCode,
            IsSystemComponent = IsHiddenByDefault(entry),
        };
    }

    /// <summary>
    /// Aturan saring PRD: SystemComponent=1, tanpa DisplayName, pembaruan (ParentKeyName/ReleaseType),
    /// serta entri non-MSI tanpa UninstallString (Windows juga tidak menampilkannya).
    /// </summary>
    public static bool IsHiddenByDefault(RawUninstallEntry entry) =>
        entry.SystemComponent
        || Clean(entry.DisplayName) is null
        || Clean(entry.ParentKeyName) is not null
        || (Clean(entry.ReleaseType) is { } releaseType && UpdateReleaseTypes.Contains(releaseType))
        || (!entry.WindowsInstaller && Clean(entry.UninstallString) is null);

    internal static Guid? TryParseProductCode(string keyName) =>
        keyName.StartsWith('{') && Guid.TryParseExact(keyName, "B", out Guid guid) ? guid : null;

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // Buang karakter kendali yang kadang tertulis oleh installer yang buruk.
        string trimmed = new string([.. value.Where(c => !char.IsControl(c))]).Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string? CleanPath(string? value)
    {
        string? path = Clean(value)?.Trim('"').Trim();
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        path = Environment.ExpandEnvironmentVariables(path);

        // "C:\Program Files\App\" → "C:\Program Files\App", tetapi "C:\" tetap.
        return path.Length > 3 ? path.TrimEnd('\\', '/') : path;
    }
}

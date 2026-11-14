using MyUninstaller.Core.Models;

namespace MyUninstaller.Core.Scanning;

/// <summary>Nilai mentah satu subkunci registri Uninstall, sebelum disaring dan dipetakan.</summary>
public sealed record RawUninstallEntry
{
    /// <summary>Nama subkunci, misalnya "7-Zip" atau "{GUID}".</summary>
    public required string KeyName { get; init; }

    public required ProgramSource Source { get; init; }

    /// <summary>Path lengkap kunci, misalnya HKEY_LOCAL_MACHINE\SOFTWARE\...\Uninstall\7-Zip.</summary>
    public required string RegistryPath { get; init; }

    public string? DisplayName { get; init; }

    public string? DisplayVersion { get; init; }

    public string? Publisher { get; init; }

    /// <summary>Nilai InstallDate apa adanya (umumnya yyyyMMdd).</summary>
    public string? InstallDate { get; init; }

    public string? InstallLocation { get; init; }

    public string? DisplayIcon { get; init; }

    public string? UninstallString { get; init; }

    public string? QuietUninstallString { get; init; }

    public string? ParentKeyName { get; init; }

    public string? ReleaseType { get; init; }

    public string? HelpLink { get; init; }

    public string? UrlInfoAbout { get; init; }

    /// <summary>EstimatedSize dalam KB.</summary>
    public long? EstimatedSizeKb { get; init; }

    public bool SystemComponent { get; init; }

    public bool WindowsInstaller { get; init; }

    /// <summary>Tanggal terakhir kunci ditulis; cadangan bila InstallDate kosong.</summary>
    public DateOnly? KeyLastWriteDate { get; init; }
}

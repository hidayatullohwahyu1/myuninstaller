namespace MyUninstaller.Core.Browsers;

public enum BrowserKind
{
    Chrome,
    Edge,
    Brave,
    Firefox,
}

/// <summary>Satu ekstensi browser di satu profil.</summary>
public sealed record BrowserExtension
{
    public required BrowserKind Browser { get; init; }

    /// <summary>Folder profil (mis. "Default", "Profile 3", "abcd.default-release").</summary>
    public required string ProfileFolder { get; init; }

    /// <summary>Nama profil yang dikenali pengguna (dari Local State / nama folder).</summary>
    public required string ProfileName { get; init; }

    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Version { get; init; }

    public string? Description { get; init; }

    public required bool IsEnabled { get; init; }

    /// <summary>Folder ekstensi (Chromium) atau berkas .xpi / folder (Firefox).</summary>
    public required string Path { get; init; }

    public long? SizeBytes { get; init; }

    public string Key => $"{Browser}|{ProfileFolder}|{Id}";
}

/// <summary>Membaca ekstensi semua browser yang dikenal (hanya baca).</summary>
public interface IBrowserExtensionReader
{
    IReadOnlyList<BrowserExtension> GetAll();
}

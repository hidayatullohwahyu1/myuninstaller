using MyUninstaller.Core.Models;

namespace MyUninstaller.Core.Portable;

/// <summary>Folder program tanpa installer (tidak tercatat di registri Uninstall maupun Store).</summary>
public sealed record PortableProgram
{
    /// <summary>Nama produk dari exe utama (ProductName/FileDescription), atau nama folder.</summary>
    public required string Name { get; init; }

    public required string Folder { get; init; }

    public required string MainExecutable { get; init; }

    public string? Publisher { get; init; }

    public string? Version { get; init; }

    public long? SizeBytes { get; init; }

    public DateTimeOffset? LastUsed { get; init; }

    /// <summary>Pintasan di Desktop/Menu Mulai yang menunjuk ke dalam folder ini (ikut dihapus).</summary>
    public IReadOnlyList<string> Shortcuts { get; init; } = [];
}

/// <summary>Info versi exe (dibaca Infrastruktur, dipakai aturan Core).</summary>
public sealed record ExecutableInfo(string Path, long Length, string? ProductName, string? Description, string? Company, string? Version);

public interface IPortableProgramScanner
{
    /// <summary>Folder yang dipindai bila pengguna tidak memilih folder sendiri.</summary>
    IReadOnlyList<string> DefaultRoots { get; }

    IReadOnlyList<PortableProgram> Scan(IReadOnlyList<string> roots, IReadOnlyList<InstalledProgram> installed, CancellationToken cancellationToken);
}

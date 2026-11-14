namespace MyUninstaller.Core.Abstractions;

/// <summary>Menghitung total ukuran isi folder (tanpa mengikuti junction/symlink).</summary>
public interface IFolderSizeCalculator
{
    /// <summary>Ukuran dalam bita, atau null bila folder tidak ada, tidak bisa dibaca, atau terlalu besar.</summary>
    long? Measure(string path, CancellationToken cancellationToken = default);
}

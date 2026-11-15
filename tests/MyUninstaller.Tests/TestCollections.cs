namespace MyUninstaller.Tests;

/// <summary>
/// Tes jendela WPF berbagi satu <c>Application.Resources</c>; bila berjalan paralel di beberapa utas STA, kamus
/// sumber daya bisa rusak ("concurrent update"). Semua kelas dengan [StaFact] masuk koleksi ini (tidak paralel).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfCollection
{
    public const string Name = "WPF";
}

/// <summary>Tes batas waktu pada sistem nyata: dijalankan tanpa tes lain bersamaan agar angkanya jujur.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingCollection
{
    public const string Name = "Waktu";
}

using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MyUninstaller.Tests.Ui;

/// <summary>
/// Memastikan semua teks antarmuka berasal dari Strings.id.resx dan tema terang/gelap konsisten.
/// Membaca berkas sumber langsung, jadi tidak perlu mereferensikan project WPF.
/// </summary>
public partial class UiResourceTests
{
    private static readonly string AppDir = Path.Combine(FindRepoRoot(), "src", "MyUninstaller.App");

    [Fact]
    public void Resx_has_no_duplicate_or_empty_keys()
    {
        var data = LoadResx().ToList();

        data.Select(d => d.Key).Should().OnlyHaveUniqueItems();
        data.Should().OnlyContain(d => !string.IsNullOrWhiteSpace(d.Value), "setiap kunci harus punya teks");
    }

    [Fact]
    public void Every_key_used_in_xaml_and_code_exists_in_resx()
    {
        var keys = LoadResx().Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (string file in Directory.EnumerateFiles(AppDir, "*.xaml", SearchOption.AllDirectories).Where(IsSource))
        {
            foreach (Match m in XamlKeyRegex().Matches(File.ReadAllText(file)))
            {
                used.Add(m.Groups[1].Value);
            }
        }

        foreach (string file in Directory.EnumerateFiles(AppDir, "*.cs", SearchOption.AllDirectories).Where(IsSource))
        {
            foreach (Match m in CodeKeyRegex().Matches(File.ReadAllText(file)))
            {
                used.Add(m.Groups[1].Value);
            }
        }

        used.Should().NotBeEmpty();
        used.Except(keys).Should().BeEmpty("kunci ini dipakai tetapi tidak ada di Strings.id.resx");
    }

    [Fact]
    public void Labels_use_sentence_case_and_group_headers_use_uppercase()
    {
        var data = LoadResx().ToDictionary(d => d.Key, d => d.Value);

        foreach (var (key, value) in data.Where(d => d.Key.StartsWith("Group_", StringComparison.Ordinal)))
        {
            value.Should().Be(value.ToUpperInvariant(), $"{key} adalah judul grup panel kiri");
        }

        // Label tombol/menu: hanya huruf pertama kapital (kecuali nama diri yang terdaftar).
        string[] properNouns = ["Pemburu", "Editor", "Registri", "Google", "Del", "Ctrl"];
        foreach (var (key, value) in data.Where(d => d.Key.StartsWith("Nav_", StringComparison.Ordinal)
                                                  || d.Key.StartsWith("Menu_", StringComparison.Ordinal)
                                                  || d.Key.StartsWith("Button_", StringComparison.Ordinal)))
        {
            var laterWords = value.Split(' ').Skip(1).Where(w => w.Length > 0 && char.IsUpper(w[0]));
            laterWords.Should().BeSubsetOf(properNouns, $"{key} = \"{value}\" harus berhuruf kapital hanya di awal");
        }
    }

    [Fact]
    public void Light_and_dark_themes_define_the_same_keys()
    {
        static HashSet<string> Keys(string file)
        {
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            return XDocument.Load(file).Root!.Elements()
                .Select(e => (string?)e.Attribute(x + "Key"))
                .OfType<string>()
                .ToHashSet();
        }

        var light = Keys(Path.Combine(AppDir, "Themes", "Colors.Light.xaml"));
        var dark = Keys(Path.Combine(AppDir, "Themes", "Colors.Dark.xaml"));

        light.Should().NotBeEmpty();
        light.Should().BeEquivalentTo(dark);
    }

    [Fact]
    public void Manifest_requires_administrator_and_per_monitor_dpi()
    {
        string manifest = File.ReadAllText(Path.Combine(AppDir, "app.manifest"));

        manifest.Should().Contain("level=\"requireAdministrator\"");
        manifest.Should().Contain("PerMonitorV2");
        manifest.Should().Contain("<longPathAware");
        manifest.Should().Contain("{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}");
    }

    private static IEnumerable<(string Key, string Value)> LoadResx()
    {
        var doc = XDocument.Load(Path.Combine(AppDir, "Resources", "Strings.id.resx"));
        return doc.Root!.Elements("data").Select(e => ((string)e.Attribute("name")!, (string?)e.Element("value") ?? string.Empty));
    }

    private static bool IsSource(string path) =>
        !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MyUninstaller.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("MyUninstaller.sln tidak ditemukan.");
    }

    [GeneratedRegex(@"\{loc:Tr\s+(?:Key=)?([A-Za-z0-9_]+)\s*\}")]
    private static partial Regex XamlKeyRegex();

    // Hanya kunci literal utuh ("Kunci" diikuti ")" atau ","). Kunci dinamis ("Kind_" + jenis) diuji terpisah
    // oleh LeftoverUiTests.Every_enum_used_for_dynamic_text_has_a_resx_key.
    [GeneratedRegex(@"Strings\.(?:Get|Format)\(\s*""([A-Za-z0-9_]+)""\s*[,)]")]
    private static partial Regex CodeKeyRegex();
}

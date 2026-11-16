using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MyUninstaller.Tests.Ui;

/// <summary>
/// Penjaga sistem desain: kontras warna (WCAG AA, 4,5:1 untuk teks kecil) dan skala tipografi. Dulu lencana berteks
/// putih hanya 2,7–3,3:1 dan ada 12 ukuran font acak.
/// </summary>
public partial class DesignSystemTests
{
    private static readonly string AppDir = Path.Combine(FindRepoRoot(), "src", "MyUninstaller.App");

    private static Dictionary<string, string> Palette(string theme)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var doc = XDocument.Load(Path.Combine(AppDir, "Themes", $"Colors.{theme}.xaml"));
        return doc.Descendants()
            .Where(e => e.Name.LocalName == "SolidColorBrush" && e.Attribute(x + "Key") is not null && e.Attribute("Color") is not null)
            .ToDictionary(e => e.Attribute(x + "Key")!.Value, e => e.Attribute("Color")!.Value);
    }

    private static double Luminance(string hex)
    {
        string h = hex.TrimStart('#');
        h = h[^6..];
        double Channel(int i)
        {
            double c = int.Parse(h.Substring(i, 2), NumberStyles.HexNumber) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(0)) + (0.7152 * Channel(2)) + (0.0722 * Channel(4));
    }

    private static double Contrast(string a, string b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void White_text_on_filled_badges_and_danger_buttons_is_readable(string theme)
    {
        var palette = Palette(theme);
        foreach (string fill in new[] { "BadgeNewBrush", "BadgeRareBrush", "BadgeRiskBrush", "DangerBrush", "DangerHoverBrush", "NeutralBadgeBrush" })
        {
            Contrast("#FFFFFF", palette[fill]).Should().BeGreaterThanOrEqualTo(4.5, $"teks putih di atas {fill} ({theme})");
        }
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Text_colors_are_readable_on_every_surface(string theme)
    {
        var palette = Palette(theme);
        foreach (string text in new[] { "TextPrimaryBrush", "TextSecondaryBrush", "DangerTextBrush", "SuccessTextBrush", "WarningTextBrush" })
        {
            foreach (string surface in new[] { "WindowBackgroundBrush", "SidebarBackgroundBrush", "SurfaceBrush", "SurfaceAltBrush" })
            {
                Contrast(palette[text], palette[surface]).Should().BeGreaterThanOrEqualTo(4.5, $"{text} di atas {surface} ({theme})");
            }
        }
    }

    [Fact]
    public void One_red_for_danger()
    {
        foreach (string theme in new[] { "Dark", "Light" })
        {
            var palette = Palette(theme);
            palette["BadgeRiskBrush"].Should().Be(palette["DangerBrush"], "lencana risiko dan tombol bahaya memakai merah yang sama");
        }
    }

    [GeneratedRegex(@"FontSize=""[0-9.]+""|Property=""FontSize"" Value=""[0-9.]+""")]
    private static partial Regex LiteralFontSize();

    [Fact]
    public void Text_uses_only_the_type_scale()
    {
        var offenders = Directory.EnumerateFiles(AppDir, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Text: line)))
            .Where(l => LiteralFontSize().IsMatch(l.Text)
                        && !l.Text.Contains("GlyphText") // ukuran ikon, bukan teks
                        && !l.Text.Contains("IconFont")
                        && !l.Text.Contains("x:Key=\"GlyphText\""))
            .Select(l => $"{l.File}:{l.Line}")
            .ToList();

        // Satu pengecualian: setter ukuran di dalam gaya GlyphText itu sendiri.
        offenders.Where(o => !o.StartsWith("Controls.xaml:", StringComparison.Ordinal) || !IsGlyphStyleLine(o)).Should().BeEmpty();
    }

    private static bool IsGlyphStyleLine(string offender)
    {
        int line = int.Parse(offender.Split(':')[1], CultureInfo.InvariantCulture);
        var lines = File.ReadAllLines(Path.Combine(AppDir, "Themes", "Controls.xaml"));
        return lines.Take(line).Reverse().TakeWhile(l => !l.Contains("<Style ")).Count() < 5
               && lines.Take(line).Reverse().FirstOrDefault(l => l.Contains("<Style "))?.Contains("GlyphText") == true;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MyUninstaller.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Akar repo tidak ditemukan.");
    }
}

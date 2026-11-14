using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using MyUninstaller.Infrastructure.Theming;

namespace MyUninstaller.App.Services;

/// <summary>
/// Mengikuti tema terang/gelap dan warna aksen Windows, termasuk saat diubah ketika aplikasi berjalan.
/// </summary>
public sealed class ThemeService(IWindowsThemeReader reader, ILogger<ThemeService> logger) : IDisposable
{
    private static readonly Color FallbackAccent = Color.FromRgb(0x3B, 0x82, 0xF6);

    private bool _initialized;

    /// <summary>Instance aktif, untuk jendela yang dibuat di luar DI (dialog).</summary>
    public static ThemeService? Current { get; private set; }

    public bool IsDark { get; private set; }

    public event EventHandler? ThemeChanged;

    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        Current = this;
        Apply();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public void Apply()
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        WindowsTheme theme = reader.Read();
        IsDark = !theme.IsLight;

        var colors = new ResourceDictionary
        {
            Source = new Uri(IsDark ? "pack://application:,,,/Themes/Colors.Dark.xaml" : "pack://application:,,,/Themes/Colors.Light.xaml"),
        };
        var merged = app.Resources.MergedDictionaries;
        if (merged.Count > 0)
        {
            merged[0] = colors;
        }
        else
        {
            merged.Add(colors);
        }

        Color accent = theme.AccentArgb is { } argb
            ? Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb)
            : FallbackAccent;

        app.Resources["AccentBrush"] = Frozen(accent);
        app.Resources["AccentHoverBrush"] = Frozen(Shade(accent, IsDark ? 0.15 : -0.12));
        // Teks beraksen harus kontras dengan latar: lebih terang di tema gelap, lebih gelap di tema terang.
        app.Resources["AccentTextBrush"] = Frozen(Shade(accent, IsDark ? 0.35 : -0.25));
        app.Resources["AccentSubtleBrush"] = Frozen(Color.FromArgb(IsDark ? (byte)0x40 : (byte)0x26, accent.R, accent.G, accent.B));

        logger.LogInformation("Tema diterapkan: {Mode}, aksen {Accent}", IsDark ? "gelap" : "terang", accent);
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_initialized)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle))
        {
            return;
        }

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                Apply();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Gagal menerapkan ulang tema");
            }
        });
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>amount &gt; 0 mencerahkan, &lt; 0 menggelapkan.</summary>
    private static Color Shade(Color c, double amount)
    {
        static byte Mix(byte channel, double amount) => amount >= 0
            ? (byte)Math.Round(channel + ((255 - channel) * amount))
            : (byte)Math.Round(channel * (1 + amount));

        return Color.FromRgb(Mix(c.R, amount), Mix(c.G, amount), Mix(c.B, amount));
    }
}

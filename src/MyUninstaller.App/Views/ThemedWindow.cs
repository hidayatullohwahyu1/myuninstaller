using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MyUninstaller.App.Interop;
using MyUninstaller.App.Services;

namespace MyUninstaller.App.Views;

/// <summary>
/// Jendela dasar yang mengikuti tema aplikasi: warna latar/teks, huruf, dan bilah judul gelap/terang.
/// </summary>
public class ThemedWindow : Window
{
    public ThemedWindow()
    {
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        SetResourceReference(FontFamilyProperty, "UiFont");
        FontSize = 13;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        SourceInitialized += (_, _) => ApplyTitleBar();

        // Jendela ber-SizeToContent kadang tidak menggambar isinya (tetap putih) dengan PerMonitorV2 +
        // bilah judul gelap. Setelah ukuran dihitung, kunci ukurannya lalu paksa gambar ulang.
        Loaded += (_, _) =>
        {
            if (SizeToContent == SizeToContent.Manual)
            {
                return;
            }

            double width = ActualWidth;
            double height = ActualHeight;
            SizeToContent = SizeToContent.Manual;
            Width = width;
            Height = height;
            InvalidateVisual();
        };
        if (ThemeService.Current is { } theme)
        {
            theme.ThemeChanged += OnThemeChanged;
            Closed += (_, _) => theme.ThemeChanged -= OnThemeChanged;
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyTitleBar();

    private void ApplyTitleBar() =>
        NativeMethods.SetDarkTitleBar(new WindowInteropHelper(this).Handle, ThemeService.Current?.IsDark ?? false);
}

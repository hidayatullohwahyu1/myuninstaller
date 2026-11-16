using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MyUninstaller.App.Interop;
using MyUninstaller.App.Services;
using MyUninstaller.Core.Layout;

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
            if (SizeToContent != SizeToContent.Manual)
            {
                double width = ActualWidth;
                double height = ActualHeight;
                SizeToContent = SizeToContent.Manual;
                Width = width;
                Height = height;
                InvalidateVisual();
            }

            FitToWorkArea(this);
        };
        if (ThemeService.Current is { } theme)
        {
            theme.ThemeChanged += OnThemeChanged;
            Closed += (_, _) => theme.ThemeChanged -= OnThemeChanged;
        }
    }

    /// <summary>
    /// Seluruh jendela (termasuk tombol di bawah) harus terlihat di atas taskbar. Batas tinggi juga dipasang agar
    /// jendela tidak bisa ditarik melewati layar.
    /// </summary>
    internal static void FitToWorkArea(Window window)
    {
        if (window.WindowState != WindowState.Normal || double.IsNaN(window.Left) || double.IsNaN(window.Top))
        {
            return;
        }

        Rect area = SystemParameters.WorkArea;
        var fitted = WindowFit.Fit(
            new WindowBounds(window.Left, window.Top, window.ActualWidth, window.ActualHeight),
            new WindowBounds(area.Left, area.Top, area.Width, area.Height));

        window.MinHeight = Math.Min(window.MinHeight, area.Height);
        window.MaxHeight = area.Height;
        if (fitted.Width < window.ActualWidth)
        {
            window.Width = fitted.Width;
        }

        if (fitted.Height < window.ActualHeight)
        {
            window.Height = fitted.Height;
        }

        window.Left = fitted.Left;
        window.Top = fitted.Top;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyTitleBar();

    private void ApplyTitleBar() =>
        NativeMethods.SetDarkTitleBar(new WindowInteropHelper(this).Handle, ThemeService.Current?.IsDark ?? false);
}

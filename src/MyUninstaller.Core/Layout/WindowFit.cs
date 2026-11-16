namespace MyUninstaller.Core.Layout;

/// <summary>Posisi dan ukuran jendela dalam DIP.</summary>
public readonly record struct WindowBounds(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;

    public double Bottom => Top + Height;
}

/// <summary>
/// Memastikan jendela muat di area kerja layar (di luar taskbar). Contoh nyata: laptop 1366×768 punya area kerja
/// setinggi 720, sedangkan jendela utama 760 dengan Top 47 membuat bilah status tertutup taskbar.
/// </summary>
public static class WindowFit
{
    /// <summary>
    /// Mengecilkan jendela yang lebih besar dari area kerja (tidak di bawah ukuran minimumnya kecuali terpaksa oleh
    /// layar), lalu menggeser jendela yang keluar tepi supaya seluruhnya terlihat.
    /// </summary>
    public static WindowBounds Fit(WindowBounds window, WindowBounds workArea)
    {
        double width = Math.Min(window.Width, workArea.Width);
        double height = Math.Min(window.Height, workArea.Height);

        double left = Math.Clamp(window.Left, workArea.Left, workArea.Right - width);
        double top = Math.Clamp(window.Top, workArea.Top, workArea.Bottom - height);
        return new WindowBounds(left, top, width, height);
    }
}

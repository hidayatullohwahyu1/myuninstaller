using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using MyUninstaller.Core.Models;
using MyUninstaller.Core.Scanning;
using MyUninstaller.Infrastructure.Scanning;

namespace MyUninstaller.App.Services;

/// <summary>
/// Memuat ikon program sebagai gambar WPF yang sudah dibekukan (aman dipakai lintas utas).
/// Aman dipanggil dari utas latar belakang; hasil disimpan dalam cache per berkas+indeks.
/// </summary>
public sealed class IconLoader(IIconPathResolver resolver, ILogger<IconLoader> logger)
{
    /// <summary>Ukuran piksel ikon yang dimuat; diperkecil WPF ke 22/48/64 DIP dengan kualitas tinggi.</summary>
    public const int IconPixelSize = 96;

    private readonly ConcurrentDictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ImageSource? Load(InstalledProgram program)
    {
        IconLocation? location = resolver.Resolve(program);
        if (location is null)
        {
            return null;
        }

        string key = $"{location.Path}|{location.Index}";
        return _cache.GetOrAdd(key, _ => Extract(location));
    }

    public void ClearCache() => _cache.Clear();

    private ImageSource? Extract(IconLocation location)
    {
        try
        {
            string extension = Path.GetExtension(location.Path);
            if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                return LoadBitmapFile(location.Path);
            }

            return IconInterop.ExtractIconImage(location.Path, location.Index, IconPixelSize);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or ArgumentException or System.Runtime.InteropServices.COMException
                                       or System.ComponentModel.Win32Exception or InvalidOperationException
                                       or FileFormatException)
        {
            logger.LogDebug(ex, "Ikon gagal dimuat: {Path},{Index}", location.Path, location.Index);
            return null;
        }
    }

    private static BitmapSource LoadBitmapFile(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad; // berkas langsung dilepas
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.DecodePixelWidth = IconPixelSize;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }
}

internal static partial class IconInterop
{
    /// <summary>Ambil ikon berukuran tertentu dari exe/dll/ico lalu salin ke bitmap beku.</summary>
    public static BitmapSource? ExtractIconImage(string path, int index, int size)
    {
        int hr = SHDefExtractIconW(path, index, 0, out IntPtr large, IntPtr.Zero, (uint)(size & 0xFFFF));
        if (hr != 0 || large == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(large, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            // Salin piksel supaya lepas dari HICON dan bisa dibekukan.
            var copy = new WriteableBitmap(source);
            copy.Freeze();
            return copy;
        }
        finally
        {
            DestroyIcon(large);
        }
    }

    [System.Runtime.InteropServices.LibraryImport("shell32.dll", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16)]
    private static partial int SHDefExtractIconW(string pszIconFile, int iIndex, uint uFlags, out IntPtr phiconLarge, IntPtr phiconSmall, uint nIconSize);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr hIcon);
}

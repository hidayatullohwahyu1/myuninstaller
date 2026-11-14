using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace MyUninstaller.Infrastructure.WindowsRegistry;

/// <summary>Pembacaan nilai registri yang toleran terhadap tipe data yang salah tulis oleh installer.</summary>
internal static partial class RegistryValues
{
    public static string? GetString(RegistryKey key, string name)
    {
        object? value = key.GetValue(name);
        return value switch
        {
            null => null,
            string s => s,
            string[] lines => string.Join(" ", lines),
            int i => i.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            _ => null, // REG_BINARY dan lainnya tidak dianggap teks.
        };
    }

    public static long? GetInt64(RegistryKey key, string name)
    {
        object? value = key.GetValue(name);
        return value switch
        {
            int i => unchecked((uint)i), // DWORD selalu tak bertanda.
            long l => l,
            string s when long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) => parsed,
            _ => null,
        };
    }

    public static bool GetFlag(RegistryKey key, string name) => GetInt64(key, name) is { } v && v != 0;

    /// <summary>Waktu terakhir kunci ditulis (RegQueryInfoKey), atau null bila gagal.</summary>
    public static DateTime? GetLastWriteTimeUtc(RegistryKey key)
    {
        try
        {
            int status = RegQueryInfoKeyW(
                key.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out long fileTime);
            return status == 0 && fileTime > 0 ? DateTime.FromFileTimeUtc(fileTime) : null;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    [LibraryImport("advapi32.dll")]
    private static partial int RegQueryInfoKeyW(
        SafeRegistryHandle hKey,
        IntPtr lpClass,
        IntPtr lpcchClass,
        IntPtr lpReserved,
        IntPtr lpcSubKeys,
        IntPtr lpcbMaxSubKeyLen,
        IntPtr lpcbMaxClassLen,
        IntPtr lpcValues,
        IntPtr lpcbMaxValueNameLen,
        IntPtr lpcbMaxValueLen,
        IntPtr lpcbSecurityDescriptor,
        out long lpftLastWriteTime);
}

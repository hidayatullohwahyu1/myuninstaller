namespace MyUninstaller.Core.Scanning;

/// <summary>
/// Konversi kode produk MSI ke bentuk "packed GUID" yang dipakai Windows Installer di
/// HKLM\SOFTWARE\Classes\Installer\Products\&lt;packed&gt;.
/// </summary>
public static class MsiGuid
{
    /// <summary>
    /// Contoh: {90120000-0011-0000-0000-0000000FF1CE} → 00002109110000000000000000F01FEC.
    /// Tiga kelompok pertama dibalik seluruhnya; sisanya dibalik per pasangan karakter.
    /// </summary>
    public static string ToPacked(Guid productCode)
    {
        string hex = productCode.ToString("N").ToUpperInvariant(); // 32 karakter tanpa tanda hubung
        var chars = new char[32];

        Reverse(hex, 0, 8, chars);
        Reverse(hex, 8, 4, chars);
        Reverse(hex, 12, 4, chars);
        for (int i = 16; i < 32; i += 2)
        {
            chars[i] = hex[i + 1];
            chars[i + 1] = hex[i];
        }

        return new string(chars);
    }

    private static void Reverse(string source, int start, int length, char[] target)
    {
        for (int i = 0; i < length; i++)
        {
            target[start + i] = source[start + length - 1 - i];
        }
    }
}

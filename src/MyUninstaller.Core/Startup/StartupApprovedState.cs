namespace MyUninstaller.Core.Startup;

/// <summary>
/// Format nilai biner di ...\Explorer\StartupApproved\{Run,Run32,StartupFolder} yang dipakai Task Manager dan
/// Pengaturan Windows: 12 bita, bita pertama genap (02/06) = aktif, ganjil (03/07) = nonaktif; bita 4–11 berisi
/// FILETIME saat dinonaktifkan. Nilai yang tidak ada berarti aktif.
/// </summary>
public static class StartupApprovedState
{
    public const int Length = 12;
    private const byte Enabled = 0x02;
    private const byte Disabled = 0x03;

    public static bool IsEnabled(byte[]? data) => data is not { Length: > 0 } || (data[0] & 0x01) == 0;

    /// <summary>Waktu dinonaktifkan, bila tercatat.</summary>
    public static DateTimeOffset? DisabledAt(byte[]? data)
    {
        if (data is not { Length: >= Length } || IsEnabled(data))
        {
            return null;
        }

        long fileTime = BitConverter.ToInt64(data, 4);
        return fileTime > 0 ? DateTimeOffset.FromFileTime(fileTime) : null;
    }

    public static byte[] Create(bool enabled, DateTimeOffset now)
    {
        var data = new byte[Length];
        data[0] = enabled ? Enabled : Disabled;
        if (!enabled)
        {
            BitConverter.GetBytes(now.ToFileTime()).CopyTo(data, 4);
        }

        return data;
    }
}

using System.Runtime.InteropServices;

namespace MyUninstaller.Infrastructure.Safety;

/// <summary>Memberi tahu jendela tingkat atas bahwa variabel lingkungan berubah (seperti dialog Variabel Lingkungan).</summary>
internal static partial class EnvironmentBroadcast
{
    private static readonly IntPtr HwndBroadcast = new(0xFFFF);
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    /// <summary>Batas 2 detik per jendela: jendela yang menggantung tidak menahan aplikasi.</summary>
    public static void Send() => SendMessageTimeout(HwndBroadcast, WmSettingChange, IntPtr.Zero, "Environment", SmtoAbortIfHung, 2000, out _);
}

using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;
using MyUninstaller.Core.Shell;

namespace MyUninstaller.App.Services;

/// <summary>
/// Pipe bernama untuk meneruskan permintaan menu klik kanan ke My Uninstaller! yang sudah terbuka.
/// Hanya grup Administrators yang boleh terhubung (aplikasi selalu berjalan dengan hak admin), sehingga proses
/// biasa tanpa elevasi tidak bisa menyuruh aplikasi ini. Pesan = satu path, divalidasi ulang oleh penerima.
/// </summary>
public sealed class InstanceChannel(string pipeName, Func<PipeSecurity> security, ILogger logger) : IDisposable
{
    private const int MaxMessageBytes = ShellRequest.MaxPathLength * 3;

    private readonly CancellationTokenSource _stop = new();

    /// <summary>Nama pipe per sesi Windows: dua pengguna yang masuk bersamaan tidak saling mengirim.</summary>
    public static string DefaultPipeName =>
        $"MyUninstaller.Perintah.{System.Diagnostics.Process.GetCurrentProcess().SessionId}";

    public static PipeSecurity AdministratorsOnly()
    {
        var pipeSecurity = new PipeSecurity();
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        return pipeSecurity;
    }

    /// <summary>Instance kedua: kirim path ke instance pertama. False bila tidak tersambung.</summary>
    public static bool Send(string pipeName, string path, int timeoutMs = 3000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            client.Connect(timeoutMs);
            byte[] bytes = Encoding.UTF8.GetBytes(path);
            client.Write(bytes);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Instance pertama: dengarkan permintaan di latar belakang sampai <see cref="Dispose"/>.</summary>
    public Task ListenAsync(Action<string> onRequest) => Task.Run(async () =>
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var server = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security());
                await server.WaitForConnectionAsync(_stop.Token);

                string? path = ShellRequest.Validate(await ReadMessageAsync(server, _stop.Token));
                if (path is null)
                {
                    logger.LogWarning("Permintaan pipe ditolak: path tidak sah");
                    continue;
                }

                logger.LogInformation("Permintaan menu klik kanan dari instance lain: {Path}", path);
                onRequest(path);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Satu permintaan rusak tidak boleh mematikan pendengar; jeda singkat agar tidak berputar cepat.
                logger.LogWarning(ex, "Galat pada pipe antar-instance");
                try
                {
                    await Task.Delay(1000, _stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    });

    private static async Task<string?> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        using var buffer = new MemoryStream();
        byte[] chunk = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxMessageBytes)
            {
                return null;
            }
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public void Dispose()
    {
        // CTS tidak di-Dispose: pendengar di latar belakang mungkin masih membaca token-nya.
        _stop.Cancel();
    }
}

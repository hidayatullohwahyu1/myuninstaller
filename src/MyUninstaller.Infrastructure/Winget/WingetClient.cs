using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using MyUninstaller.Core.Winget;

namespace MyUninstaller.Infrastructure.Winget;

/// <summary>
/// Menjalankan winget.exe (versi 2.0). Daftar dibaca tanpa interaksi (<c>--disable-interactivity</c>, keluaran
/// ditangkap, batas waktu). Pembaruan/pasang ulang dijalankan di jendela konsol yang terlihat: pengguna melihat
/// kemajuan dan menyetujui ketentuan sumber/lisensi paket sendiri; aplikasi tidak pernah menyetujui atas namanya.
/// </summary>
public sealed class WingetClient(ILogger<WingetClient> logger) : IWingetClient
{
    private static readonly TimeSpan ListTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Konsol pembaruan bisa lama (unduhan besar), tetapi tidak tak terbatas.</summary>
    private static readonly TimeSpan ConsoleTimeout = TimeSpan.FromHours(2);

    /// <summary>winget.exe adalah alias eksekusi aplikasi di %LocalAppData%\Microsoft\WindowsApps.</summary>
    public static string? FindWinget()
    {
        string alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        return File.Exists(alias) ? alias : null;
    }

    public async Task<WingetListResult> ListAsync(CancellationToken cancellationToken)
    {
        if (FindWinget() is not { } winget)
        {
            return new WingetListResult([], null, WingetMissing: true);
        }

        var start = new ProcessStartInfo(winget, "list --source winget --disable-interactivity")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ListTimeout);
        Process? process = null;
        try
        {
            process = Process.Start(start);
            if (process is null)
            {
                return new WingetListResult([], null, WingetMissing: true);
            }

            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            string output = await stdout;
            string errors = await stderr;

            var packages = WingetRules.ParseList(output);
            logger.LogInformation("winget list: kode {Code}, {Count} paket", process.ExitCode, packages.Count);
            if (process.ExitCode != 0 && packages.Count == 0)
            {
                return new WingetListResult([], WingetRules.LastMessage(errors + "\n" + output) ?? $"0x{process.ExitCode:X8}");
            }

            return new WingetListResult(packages, null);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            logger.LogWarning("winget list melewati batas waktu");
            return new WingetListResult([], "Batas waktu habis.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            logger.LogWarning(ex, "winget tidak bisa dijalankan");
            return new WingetListResult([], null, WingetMissing: true);
        }
        finally
        {
            process?.Dispose();
        }
    }

    public async Task<int?> RunInConsoleAsync(WingetAction action, string? packageId, CancellationToken cancellationToken)
    {
        if (FindWinget() is not { } winget)
        {
            return null;
        }

        // ID sudah divalidasi (huruf/angka/.-_+), jadi aman disisipkan ke baris perintah cmd.
        string arguments = WingetRules.Arguments(action, packageId);
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var start = new ProcessStartInfo(cmd, $"/d /c \"\"{winget}\" {arguments} & echo. & pause\"")
        {
            UseShellExecute = false,
            CreateNoWindow = false, // Aplikasi GUI tanpa konsol: proses anak mendapat jendela konsol baru yang terlihat.
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConsoleTimeout);
        Process? process = null;
        try
        {
            logger.LogInformation("winget {Arguments} dijalankan di konsol", arguments);
            process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            await process.WaitForExitAsync(timeout.Token);
            logger.LogInformation("Konsol winget selesai, kode {Code}", process.ExitCode);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            // Jendela konsol dibiarkan (bisa sedang memasang); aplikasi berhenti menunggu saja.
            logger.LogInformation("Berhenti menunggu konsol winget");
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "Konsol winget tidak bisa dibuka");
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void TryKill(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Sudah selesai.
        }
    }
}

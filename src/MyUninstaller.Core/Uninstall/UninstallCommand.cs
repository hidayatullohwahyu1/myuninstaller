using System.Text.RegularExpressions;
using MyUninstaller.Core.Models;

namespace MyUninstaller.Core.Uninstall;

/// <summary>Jenis perintah hapus instalan.</summary>
public enum UninstallCommandKind
{
    /// <summary>Menjalankan berkas .exe penghapus instalan.</summary>
    Executable,

    /// <summary>msiexec /x {ProductCode}.</summary>
    WindowsInstaller,

    /// <summary>Paket Store: dihapus lewat PackageManager.</summary>
    StorePackage,

    /// <summary>Baris perintah lain (misalnya .bat/.cmd) yang dijalankan lewat cmd.exe.</summary>
    Shell,
}

/// <summary>Perintah siap jalan.</summary>
/// <param name="FileName">Berkas yang dijalankan (path lengkap untuk exe, "msiexec.exe", atau PackageFullName untuk Store).</param>
/// <param name="Arguments">Argumen baris perintah.</param>
/// <param name="Kind">Jenis perintah.</param>
/// <param name="IsSilent">True bila perintah berjalan tanpa interaksi pengguna.</param>
public sealed record UninstallCommand(string FileName, string Arguments, UninstallCommandKind Kind, bool IsSilent);

/// <summary>
/// Menyusun perintah hapus instalan dari entri program. Logika murni; keberadaan berkas diperiksa lewat
/// delegasi agar mudah dites.
/// </summary>
public static partial class UninstallCommandBuilder
{
    /// <summary>
    /// Membuat perintah, atau null bila tidak ada cara menjalankan penghapus instalan
    /// (entri rusak: gunakan Hapus paksa).
    /// </summary>
    /// <param name="program">Program yang akan dihapus.</param>
    /// <param name="silent">Minta mode senyap (dipakai bila ada cara senyap yang terdokumentasi).</param>
    /// <param name="fileExists">Pemeriksa keberadaan berkas.</param>
    /// <param name="detectKind">Opsional: mengenali NSIS/Inno dari isi exe untuk parameter senyapnya.</param>
    public static UninstallCommand? Build(
        InstalledProgram program,
        bool silent,
        Func<string, bool> fileExists,
        Func<string, InstallerKind>? detectKind = null)
    {
        if (program.Source == ProgramSource.Store)
        {
            string fullName = program.Id.StartsWith("Store:", StringComparison.Ordinal) ? program.Id["Store:".Length..] : program.Id;
            return new UninstallCommand(fullName, string.Empty, UninstallCommandKind.StorePackage, IsSilent: true);
        }

        string? commandLine = silent && !string.IsNullOrWhiteSpace(program.QuietUninstallString)
            ? program.QuietUninstallString
            : program.UninstallString;
        bool usesQuietString = silent && ReferenceEquals(commandLine, program.QuietUninstallString);

        // MSI: selalu ubah ke /x {GUID} (UninstallString MSI sering berisi /I yang membuka mode ubah/perbaiki).
        Guid? productCode = ExtractMsiProductCode(commandLine) ?? (program.IsWindowsInstaller ? program.ProductCode : null);
        if (productCode is { } code && (IsMsiExec(commandLine) || string.IsNullOrWhiteSpace(commandLine)))
        {
            return Msi(code, silent);
        }

        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        if (!TrySplit(Environment.ExpandEnvironmentVariables(commandLine.Trim()), fileExists, out string file, out string args))
        {
            return null;
        }

        string extension = Path.GetExtension(file);
        if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            // .bat/.cmd dan lainnya: lewat cmd.exe.
            return new UninstallCommand("cmd.exe", $"/c \"{commandLine.Trim()}\"", UninstallCommandKind.Shell, usesQuietString);
        }

        bool isSilent = usesQuietString;
        if (silent && !usesQuietString)
        {
            // Nama unins000.exe sudah pasti Inno Setup; selain itu kenali dari isi berkas bila pendeteksi tersedia.
            InstallerKind kind = InnoSetupRegex().IsMatch(Path.GetFileName(file))
                ? InstallerKind.InnoSetup
                : detectKind?.Invoke(file) ?? InstallerKind.Unknown;

            switch (kind)
            {
                case InstallerKind.InnoSetup:
                    args = AppendArgs(args, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART");
                    isSilent = true;
                    break;

                // NSIS: /S peka huruf besar; jangan ditambah dua kali.
                case InstallerKind.Nsis when !HasSwitch(args, "/S"):
                    args = AppendArgs(args, "/S");
                    isSilent = true;
                    break;

                case InstallerKind.Nsis:
                    isSilent = true;
                    break;
            }
        }

        return new UninstallCommand(file, args, UninstallCommandKind.Executable, isSilent);
    }

    /// <summary>
    /// Ambil GUID dari perintah msiexec, misalnya "MsiExec.exe /I{GUID}" atau "msiexec /x {GUID} /qb".
    /// </summary>
    public static Guid? ExtractMsiProductCode(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || !IsMsiExec(commandLine))
        {
            return null;
        }

        Match match = MsiGuidRegex().Match(commandLine);
        return match.Success && Guid.TryParse(match.Groups[1].Value, out Guid guid) ? guid : null;
    }

    public static UninstallCommand Msi(Guid productCode, bool silent) => new(
        "msiexec.exe",
        silent ? $"/x {productCode:B} /qn /norestart" : $"/x {productCode:B}",
        UninstallCommandKind.WindowsInstaller,
        silent);

    private static bool IsMsiExec(string? commandLine) =>
        commandLine is not null && commandLine.Contains("msiexec", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Memisahkan berkas dan argumen. Mendukung path berkutip, dan path tanpa kutip yang mengandung spasi
    /// (misalnya C:\Program Files\App\uninst.exe /S) dengan mencari batas ".exe" yang berkasnya ada.
    /// </summary>
    internal static bool TrySplit(string commandLine, Func<string, bool> fileExists, out string file, out string args)
    {
        file = string.Empty;
        args = string.Empty;

        if (commandLine.StartsWith('"'))
        {
            int close = commandLine.IndexOf('"', 1);
            if (close < 0)
            {
                return false;
            }

            file = commandLine[1..close];
            args = commandLine[(close + 1)..].Trim();
            return file.Length > 0;
        }

        // Coba setiap kemunculan ".exe" dari kiri; pakai yang pertama berkasnya ada.
        int searchFrom = 0;
        while (true)
        {
            int at = commandLine.IndexOf(".exe", searchFrom, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                break;
            }

            int end = at + 4;
            if (end == commandLine.Length || commandLine[end] == ' ')
            {
                string candidate = commandLine[..end];
                if (fileExists(candidate) || !candidate.Contains(' '))
                {
                    file = candidate;
                    args = commandLine[end..].Trim();
                    return true;
                }
            }

            searchFrom = end;
        }

        // Tanpa .exe (misalnya .bat): token pertama.
        int space = commandLine.IndexOf(' ');
        file = space < 0 ? commandLine : commandLine[..space];
        args = space < 0 ? string.Empty : commandLine[(space + 1)..].Trim();
        return file.Length > 0;
    }

    private static bool HasSwitch(string args, string name) =>
        args.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(a => a.Equals(name, StringComparison.Ordinal));

    private static string AppendArgs(string args, string extra) => string.IsNullOrWhiteSpace(args) ? extra : $"{args} {extra}";

    [GeneratedRegex(@"\{?([0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})\}?")]
    private static partial Regex MsiGuidRegex();

    [GeneratedRegex(@"^unins\d{3}\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex InnoSetupRegex();
}

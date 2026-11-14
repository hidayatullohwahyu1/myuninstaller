using MyUninstaller.Core.Models;
using MyUninstaller.Core.Safety;
using MyUninstaller.Core.Scanning;

namespace MyUninstaller.Core.Uninstall;

/// <summary>Menjalankan proses penghapus instalan dan menunggu proses anaknya (Job Object).</summary>
public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(UninstallCommand command, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Menghapus paket Store/UWP.</summary>
public interface IStorePackageRemover
{
    Task<ProcessRunResult> RemoveAsync(string packageFullName, CancellationToken cancellationToken);
}

/// <summary>Memeriksa kondisi instalasi sesudah hapus instalan (hanya baca).</summary>
public interface IInstallStateProbe
{
    /// <summary>Entri registri atau paket Store program masih ada.</summary>
    bool EntryExists(InstalledProgram program);

    /// <summary>Folder masih ada dan berisi minimal satu berkas.</summary>
    bool FolderHasFiles(string path);

    bool FileExists(string path);
}

/// <summary>Daftar dan penutupan proses yang sedang berjalan.</summary>
public interface IProcessInspector
{
    IReadOnlyList<RunningProcess> GetRunningProcesses();

    /// <summary>Menutup paksa satu proses. Proses kritis selalu ditolak.</summary>
    bool TryKill(RunningProcess process);
}

/// <summary>
/// Pelaksana hapus instalan untuk satu program: susun perintah → jalankan → verifikasi.
/// Pemeriksaan awal (proses berjalan) dan titik pemulihan diatur oleh pemanggil.
/// </summary>
public sealed class UninstallRunner(
    IProcessRunner processRunner,
    IStorePackageRemover storeRemover,
    IInstallStateProbe probe,
    InstallLocationRules locationRules,
    IInstallerKindDetector? kindDetector = null)
{
    /// <summary>Batas waktu satu penghapus instalan (PRD: 30 menit).</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(30);

    /// <summary>Setelah selama ini, beri tahu bahwa penghapus instalan mungkin menunggu masukan.</summary>
    public static readonly TimeSpan WaitingForInputHint = TimeSpan.FromMinutes(2);

    public UninstallCommand? PlanCommand(InstalledProgram program, bool silent) =>
        UninstallCommandBuilder.Build(program, silent, probe.FileExists,
            kindDetector is null ? null : path => probe.FileExists(path) ? kindDetector.Detect(path) : InstallerKind.Unknown);

    public async Task<UninstallResult> RunAsync(InstalledProgram program, bool silent, CancellationToken cancellationToken)
    {
        UninstallCommand? command = PlanCommand(program, silent);
        if (command is null)
        {
            return new UninstallResult(UninstallOutcome.NoUninstaller, null, false, TimeSpan.Zero);
        }

        if (command.Kind == UninstallCommandKind.Executable && !probe.FileExists(command.FileName))
        {
            return new UninstallResult(UninstallOutcome.NoUninstaller, null, false, TimeSpan.Zero, command.FileName);
        }

        ProcessRunResult run = command.Kind == UninstallCommandKind.StorePackage
            ? await storeRemover.RemoveAsync(command.FileName, cancellationToken).ConfigureAwait(false)
            : await processRunner.RunAsync(command, Timeout, cancellationToken).ConfigureAwait(false);

        // Beri waktu sebentar: beberapa uninstaller menghapus entri registri sesaat setelah proses keluar.
        bool entryExists = probe.EntryExists(program);
        if (entryExists && !run.Cancelled)
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5), CancellationToken.None).ConfigureAwait(false);
            entryExists = probe.EntryExists(program);
        }

        bool folderHasFiles = locationRules.IsMeasurable(program.InstallLocation) && probe.FolderHasFiles(program.InstallLocation!);
        UninstallOutcome outcome = UninstallEvaluator.Evaluate(run, entryExists, folderHasFiles);

        return new UninstallResult(outcome, run.ExitCode, UninstallEvaluator.IsRestartRequired(run.ExitCode), run.Elapsed);
    }
}

using System.Text.RegularExpressions;
using MyUninstaller.Core.Models;

namespace MyUninstaller.Core.Safety;

/// <summary>Jenis program yang berisiko bila dihapus (PRD: butuh peringatan keras).</summary>
public enum ProgramRisk
{
    None,

    /// <summary>Paket driver perangkat keras (touchpad, printer, grafis, dll.).</summary>
    Driver,

    /// <summary>Runtime yang dipakai banyak program: .NET, Visual C++ Redistributable, WebView2, Java, DirectX.</summary>
    SharedRuntime,

    /// <summary>Komponen sistem Windows (SystemComponent=1 atau aplikasi Store bawaan).</summary>
    SystemComponent,
}

/// <summary>
/// Aturan PRD: "Tidak menghapus komponen sistem Windows, driver, atau runtime yang dipakai banyak program
/// tanpa peringatan keras." Hanya mengklasifikasi; keputusan tetap di tangan pengguna.
/// </summary>
public static partial class ProgramRiskRules
{
    public static ProgramRisk Classify(InstalledProgram program)
    {
        string name = program.DisplayName;
        string uninstall = program.UninstallString ?? string.Empty;

        if (DriverNameRegex().IsMatch(name)
            || DriverCommandRegex().IsMatch(uninstall)
            || name.StartsWith("Windows Driver Package", StringComparison.OrdinalIgnoreCase))
        {
            return ProgramRisk.Driver;
        }

        if (RuntimeNameRegex().IsMatch(name))
        {
            return ProgramRisk.SharedRuntime;
        }

        if (program.IsSystemComponent)
        {
            return ProgramRisk.SystemComponent;
        }

        return ProgramRisk.None;
    }

    // "Windows Driver Package - ...", "... Driver", "Graphics Driver", "MP Drivers", "Touchpad"
    [GeneratedRegex(@"\bdrivers?\b|\btouch\s?pad\b|\bchipset\b|\bfirmware\b", RegexOptions.IgnoreCase)]
    private static partial Regex DriverNameRegex();

    // Uninstaller driver bawaan Windows (DPInst/DrvInst/pnputil) atau berkas .inf.
    [GeneratedRegex(@"dpinst|drvinst|pnputil|\.inf\b", RegexOptions.IgnoreCase)]
    private static partial Regex DriverCommandRegex();

    [GeneratedRegex(
        @"Visual C\+\+ .*Redistributable|\.NET (Core )?Runtime|\.NET Desktop Runtime|\.NET Framework|ASP\.NET Core .*(Shared Framework|Runtime)|Windows Desktop Runtime|WebView2 Runtime|DirectX|Java\(TM\) SE Runtime|Java .*Runtime|\bOpenAL\b|Microsoft XNA Framework|Visual Studio .*Redistributable|VC_?redist|Universal C Runtime",
        RegexOptions.IgnoreCase)]
    private static partial Regex RuntimeNameRegex();
}

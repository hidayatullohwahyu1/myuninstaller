namespace MyUninstaller.Core.Uninstall;

/// <summary>Teknologi pembuat penghapus instalan, untuk menentukan parameter senyap yang terdokumentasi.</summary>
public enum InstallerKind
{
    Unknown,

    /// <summary>Nullsoft Scriptable Install System: senyap dengan /S.</summary>
    Nsis,

    /// <summary>Inno Setup: senyap dengan /VERYSILENT /SUPPRESSMSGBOXES /NORESTART.</summary>
    InnoSetup,
}

/// <summary>Mengenali jenis penghapus instalan dari isi berkas exe (hanya baca).</summary>
public interface IInstallerKindDetector
{
    InstallerKind Detect(string executablePath);
}

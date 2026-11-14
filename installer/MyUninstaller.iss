; Installer My Uninstaller! (Inno Setup 6).
; Dibangun lewat build-rilis.ps1 di akar repo, yang lebih dulu menjalankan tes dan publish ke artifacts\publish.
; Varian framework-dependent: butuh .NET 10 Desktop Runtime x64 (diperiksa saat memasang).

#define AppName "My Uninstaller!"
#define AppExe "MyUninstaller.exe"
#define PublishDir AddBackslash(SourcePath) + "..\artifacts\publish"
; Versi produk 3 bagian (mis. 1.0.0) dari exe; nomor 4 bagian dipakai untuk metadata berkas.
#define AppVersion GetStringFileInfo(PublishDir + "\" + AppExe, "ProductVersion")
#define AppFileVersion GetVersionNumbersString(PublishDir + "\" + AppExe)

[Setup]
; GUID tetap: jangan diubah, supaya versi baru memperbarui instalasi lama (bukan dipasang dobel).
AppId={{6E3F2A41-8B7C-4D19-A5E2-3C9F1B7D0A58}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Pemakaian pribadi
VersionInfoVersion={#AppFileVersion}
VersionInfoProductName={#AppName}
DefaultDirName={autopf}\MyUninstaller
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=MyUninstaller-{#AppVersion}-setup
SetupIconFile=..\src\MyUninstaller.App\Assets\MyUninstaller.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

; Aplikasi selalu berjalan sebagai Administrator (manifest requireAdministrator).
PrivilegesRequired=admin
; Windows 10 1809+ dan Windows 11, hanya 64-bit (PRD).
MinVersion=10.0.17763
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Tutup My Uninstaller! yang sedang berjalan sebelum memperbarui/menghapus (mutex satu instance di App.xaml.cs).
AppMutex=Local\MyUninstaller.SingleInstance.{{B8E5D2A4-6C1F-4E7B-9A3D-2F8C1E5B7A90}
CloseApplications=yes

[Languages]
#ifexist "compiler:Languages\Indonesian.isl"
Name: "id"; MessagesFile: "compiler:Languages\Indonesian.isl"
#else
Name: "id"; MessagesFile: "compiler:Default.isl"
#endif

[CustomMessages]
id.DesktopIcon=Buat pintasan di Desktop
id.Shortcuts=Pintasan:
id.ContextMenu=Tambahkan "Hapus instalan dengan My Uninstaller!" ke menu klik kanan berkas .exe dan pintasan
id.ContextMenuVerb=Hapus instalan dengan My Uninstaller!
id.Integration=Integrasi Explorer:
id.LaunchApp=Jalankan My Uninstaller! sekarang
id.RuntimeMissing=My Uninstaller! membutuhkan .NET 10 Desktop Runtime (x64), yang belum terpasang di komputer ini.%n%nBuka halaman unduhan Microsoft sekarang? Setelah runtime terpasang, jalankan installer ini lagi.
id.KeepData=Hapus juga riwayat, catatan aktivitas, dan karantina My Uninstaller!?%n%n%1%n%nKarantina bisa berisi berkas yang belum dipulihkan. Pilih "No" untuk menyimpannya.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:Shortcuts}"; Flags: unchecked
Name: "contextmenu"; Description: "{cm:ContextMenu}"; GroupDescription: "{cm:Integration}"

[Files]
Source: "{#PublishDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

; Menu klik kanan (versi 2.0). Hanya menambah subkunci milik aplikasi ini di bawah shell exefile/lnkfile
; (bukan mengubah kunci yang ada); dihapus lagi saat uninstall. Di Windows 11 ada di "Tampilkan opsi lainnya".
[Registry]
Root: HKLM; Subkey: "Software\Classes\exefile\shell\MyUninstaller"; ValueType: string; ValueName: ""; ValueData: "{cm:ContextMenuVerb}"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKLM; Subkey: "Software\Classes\exefile\shell\MyUninstaller"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"",0"; Tasks: contextmenu
Root: HKLM; Subkey: "Software\Classes\exefile\shell\MyUninstaller\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" --hapus ""%1"""; Tasks: contextmenu
Root: HKLM; Subkey: "Software\Classes\lnkfile\shell\MyUninstaller"; ValueType: string; ValueName: ""; ValueData: "{cm:ContextMenuVerb}"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKLM; Subkey: "Software\Classes\lnkfile\shell\MyUninstaller"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"",0"; Tasks: contextmenu
Root: HKLM; Subkey: "Software\Classes\lnkfile\shell\MyUninstaller\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" --hapus ""%1"""; Tasks: contextmenu

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[Code]
const
  DotNetDownloadUrl = 'https://dotnet.microsoft.com/download/dotnet/10.0';

{ .NET 10 Desktop Runtime x64 terpasang bila ada folder shared\Microsoft.WindowsDesktop.App\10.* }
function IsDesktopRuntime10Installed(): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
          Result := True;
      until Result or not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if not IsDesktopRuntime10Installed() then
  begin
    if MsgBox(CustomMessage('RuntimeMissing'), mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', DotNetDownloadUrl, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    Result := False;
  end;
end;

{ Data aplikasi (%ProgramData%\MyUninstaller) TIDAK dihapus otomatis: karantina bisa berisi berkas pengguna.
  Pengguna ditanya, bawaannya "No". }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{commonappdata}\MyUninstaller');
    if DirExists(DataDir) and not UninstallSilent() then
      if MsgBox(FmtMessage(CustomMessage('KeepData'), [DataDir]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;

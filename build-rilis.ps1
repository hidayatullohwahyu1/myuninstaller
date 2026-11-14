<#
.SYNOPSIS
    Membangun rilis My Uninstaller!: tes → publish single-file → installer Inno Setup.

.DESCRIPTION
    1. dotnet test (dilewati dengan -LewatiTes). Tes integrasi hanya membaca registri/paket mesin ini, dan
       memakai folder sementara serta kunci HKCU\Software\MyUninstallerTests yang dihapus kembali.
    2. dotnet publish dengan profil Rilis → artifacts\publish\MyUninstaller.exe
    3. ISCC installer\MyUninstaller.iss → artifacts\installer\MyUninstaller-<versi>-setup.exe
       (dilewati dengan pesan bila Inno Setup 6 belum terpasang).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\build-rilis.ps1
#>
param(
    [switch]$LewatiTes
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'

function Langkah($teks) { Write-Host "`n== $teks" -ForegroundColor Cyan }

if (-not $LewatiTes) {
    Langkah 'Tes'
    dotnet test (Join-Path $root 'MyUninstaller.sln') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tes gagal; rilis dihentikan.' }
}

Langkah 'Publish single-file'
Remove-Item (Join-Path $artifacts 'publish') -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish (Join-Path $root 'src\MyUninstaller.App') -p:PublishProfile=Rilis --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish gagal.' }

$exe = Join-Path $artifacts 'publish\MyUninstaller.exe'
$versi = (Get-Item $exe).VersionInfo.ProductVersion
Write-Host ("MyUninstaller.exe {0}: {1:N1} MB" -f $versi, ((Get-Item $exe).Length / 1MB))

Langkah 'Installer'
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Warning 'Inno Setup 6 belum terpasang; installer dilewati. Pasang dengan: winget install JRSoftware.InnoSetup'
    return
}

& $iscc /Qp (Join-Path $root 'installer\MyUninstaller.iss')
if ($LASTEXITCODE -ne 0) { throw 'Kompilasi installer gagal.' }

$setup = Get-ChildItem (Join-Path $artifacts 'installer') -Filter 'MyUninstaller-*-setup.exe' | Sort-Object LastWriteTime | Select-Object -Last 1
Write-Host ("Installer: {0} ({1:N1} MB)" -f $setup.FullName, ($setup.Length / 1MB)) -ForegroundColor Green
if ($setup.Length -gt 15MB) { Write-Warning 'Installer melebihi target PRD 15 MB.' }

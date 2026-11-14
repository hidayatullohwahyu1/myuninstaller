# CLAUDE.md — My Uninstaller!

Aplikasi uninstaller Windows 10/11 berbahasa Indonesia (C# 14, .NET 10 LTS, WPF, MVVM) untuk pemakaian pribadi.
Menghapus program sampai bersih (berkas, folder, registri, layanan, tugas terjadwal) dengan aman, lengkap
dengan cadangan dan tombol Batalkan. Spesifikasi lengkap: `PRD My Uninstaller! — Aplikasi Uninstaller Windows.md`.

## Perintah

```
dotnet build MyUninstaller.sln
dotnet test MyUninstaller.sln
```

Setiap tahap wajib lolos `dotnet build` (tanpa peringatan; `TreatWarningsAsErrors` aktif) dan `dotnet test`.
Uji hapus instalan sungguhan hanya di mesin virtual dengan snapshot, jangan di laptop kerja.

Aplikasi memakai manifest `requireAdministrator`. Untuk sekadar melihat tampilan tanpa UAC saat pengembangan,
jalankan dengan variabel lingkungan `__COMPAT_LAYER=RunAsInvoker` (fitur yang butuh admin akan gagal).

## Struktur

- `src/MyUninstaller.App` — WPF: Views, ViewModels, Services (tema, dialog, shell), `Resources/Strings.id.resx`,
  `Themes/` (Colors.Light/Dark + Controls), `app.manifest`, `Assets/MyUninstaller.ico`.
- `src/MyUninstaller.Core` — model, interface, dan logika murni (tanpa WPF/Win32) supaya mudah dites:
  `Models/`, `Abstractions/`, `Formatting/`, `Listing/`, `Settings/`; nanti `Scanning/`, `Uninstall/`, `Safety/`.
- `src/MyUninstaller.Infrastructure` — registri, berkas, WMI, SQLite, P/Invoke. Semua akses API Windows di sini.
- `tests/MyUninstaller.Tests` — xUnit + FluentAssertions 7.x (jangan naik ke 8.x: lisensi berbayar).

DI lewat `Microsoft.Extensions.Hosting` di `App.xaml.cs`; layanan Infrastruktur didaftarkan di
`ServiceCollectionExtensions.AddInfrastructure()`. Log Serilog harian di `%ProgramData%\MyUninstaller\Log`.
Pengaturan tampilan per pengguna di `%LocalAppData%\MyUninstaller\pengaturan.json`.

## Aturan keamanan (wajib, tidak boleh dilanggar)

- **Jangan pernah menghapus permanen tanpa karantina.** Berkas/folder dipindah ke
  `%ProgramData%\MyUninstaller\Karantina\<tanggal-sesi>`; kunci registri diekspor ke `.reg` dulu.
- Setiap aksi dicatat ke jurnal sesi (SQLite) **sebelum** dijalankan.
- Daftar lindung dicek ulang **tepat sebelum** menghapus, bukan hanya saat memindai.
- Path dan kunci yang tidak pernah disentuh:
  - `C:\Windows` (termasuk `System32`, `SysWOW64`, `WinSxS`)
  - `C:\Program Files\Common Files`, `C:\Program Files (x86)\Common Files`
  - Folder `Microsoft` bersama di Program Files, ProgramData, dan AppData
  - `HKLM\SYSTEM\CurrentControlSet\Control`
  - Kunci milik TrustedInstaller, kunci registri milik Microsoft
  - Kunci induk penerbit yang masih dipakai program lain dari penerbit yang sama
  - Layanan/driver bawaan Microsoft
- Jika akses ditolak: lewati item. Jangan mengambil alih kepemilikan (take ownership).
- Cek `SharedDLLs` dan referensi Windows Installer; berkas bersama yang masih dipakai tidak dihapus.
- Proses kritis (csrss, wininit, winlogon, lsass, services, svchost) tidak pernah dihentikan;
  explorer.exe hanya dimulai ulang dengan persetujuan pengguna.
- Tidak ada "pembersih registri" umum; hanya kunci yang jelas terkait program yang dihapus.
- Aplikasi tidak memasang driver, tidak menyuntik kode, tidak mengubah berkas sistem, tidak mengirim data apa pun.
- Registri dibaca eksplisit dengan `RegistryView.Registry64` dan `RegistryView.Registry32`.
- Nilai dari registri tidak tepercaya: tautan hanya dibuka bila http/https (lihat `ShellService.OpenUrl`).

## Catatan pemindai

- Aturan saring meniru "Aplikasi terinstal" Windows: SystemComponent=1, tanpa DisplayName, ParentKeyName,
  ReleaseType pembaruan, dan entri non-MSI tanpa UninstallString disembunyikan.
- Properti `Package` Store (DisplayName, GetAppListEntries, Logo) lambat; jangan akses berurutan di UI thread.
  Tanggal pasang Store diambil dari waktu pembuatan folder paket.
- HKCU yang dibaca adalah milik akun yang menjalankan aplikasi (admin). Bila Windows dipakai akun standar
  lalu elevasi pakai akun admin lain, program per-pengguna akun standar tidak terlihat (keterbatasan diketahui).
- Ukuran folder tidak dihitung untuk folder bersama (akar drive, Program Files, Windows, profil pengguna);
  lihat `InstallLocationRules`. Junction/symlink tidak diikuti.

## Ketangguhan

- Semua pemindaian/penghapusan async di latar belakang, bisa dibatalkan (`CancellationToken`).
- Setiap item dibungkus try/catch sendiri; satu kegagalan tidak menghentikan pemindaian atau antrean.
- Batas waktu untuk setiap proses luar. Ikon/entri rusak tampil dengan ikon bawaan, bukan galat.
- Penangan galat global sudah ada di `App.xaml.cs`. Aplikasi hanya satu instance (mutex).
- Jendela baru turunkan dari `Views/ThemedWindow` (tema + bilah judul gelap + perbaikan `SizeToContent` yang
  tanpa itu tampil putih). Dialog pesan pakai `ChoiceDialog` (label tombol bahasa Indonesia, tombol Danger tidak
  pernah jadi tombol Enter), bukan MessageBox.
- Job Object memakai `BREAKAWAY_OK` dan **tidak** `KILL_ON_JOB_CLOSE`: bila aplikasi tertutup, uninstaller harus tetap selesai.

## Catatan pengembangan

- Jangan menulis path Windows lewat skrip Python/sed dengan string biasa: `\0`, `\r`, `\a` berubah jadi karakter
  kendali. Pakai tool Edit, atau string mentah (r"...").

## Gaya kode

- C# 14, nullable aktif, `file-scoped namespace`, primary constructor bila sederhana.
- MVVM dengan CommunityToolkit.Mvvm (`[ObservableProperty]` pada partial property, `[RelayCommand]`).
- Logika yang bisa dites taruh di Core; ViewModel tipis. Tambah unit test untuk setiap logika baru.
- Komentar dan pesan log dalam bahasa Indonesia. Nama tipe/anggota dalam bahasa Inggris.
- WPF: `System.IO` tidak termasuk implicit using di project App — tambahkan `using System.IO;` bila perlu.

## Bahasa antarmuka (KBBI, EYD Edisi V)

- **Semua teks antarmuka di `src/MyUninstaller.App/Resources/Strings.id.resx`.** Di XAML pakai
  `{loc:Tr Kunci}`, di kode pakai `Strings.Get("Kunci")` / `Strings.Format("Kunci", ...)`.
  Tes `UiResourceTests` gagal bila kunci yang dipakai tidak ada di resx.
- Label tombol/menu: huruf kapital hanya di awal ("Hapus instalan"). Judul grup panel kiri kapital semua.
- Pesan pendek, aktif, sopan, tanpa "Anda" berulang. Contoh: "Program berhasil dihapus. 12 berkas sisa dibersihkan."
- Desimal koma (38,2 GB), ribuan titik (1.250 berkas), tanggal "2 September 2026", jam "14.30" —
  gunakan `IndonesianFormat`.
- Tombol berbahaya merah (`DangerButton`) dan tidak pernah tombol bawaan Enter.

| Istilah Inggris | Dipakai di aplikasi |
| --- | --- |
| Uninstall | Hapus instalan |
| Force uninstall / Quick uninstall | Hapus paksa |
| Installed programs | Program terpasang |
| Leftovers | Sisa instalan |
| Scan | Pindai |
| Hunter Mode | Mode Pemburu |
| AutoFix | Perbaikan otomatis |
| Startup manager | Program awal |
| Registry | Registri |
| Registry Editor | Editor Registri |
| File / Folder | Berkas / Folder |
| Backup | Cadangan |
| Restore point | Titik pemulihan |
| Restore / Undo | Pulihkan / Batalkan |
| Quarantine | Karantina |
| Log | Catatan aktivitas |
| Refresh | Segarkan |
| Details view | Tampilan rincian |
| Settings | Pengaturan |
| Silent mode | Mode senyap |
| Status bar | Bilah status |

## Status tahapan

1. [x] Kerangka proyek: solution 4 project, DI, Serilog, manifest admin, jendela utama bergaya Your Uninstaller!
   (panel kiri, kisi ikon/rincian, panel info, bilah status), tema terang/gelap + aksen Windows.
2. [x] Pemindai program: `RegistryUninstallReader` (HKLM 64/32, HKCU) + `StorePackageReader` (paralel),
   dipetakan/disaring di Core (`UninstallEntryMapper`, `ProgramDeduplicator`). Komponen sistem tetap dipindai
   tetapi disembunyikan (opsi "Komponen sistem" di panel kiri). Ikon (`IconPathResolver` + `IconLoader`) dan
   ukuran folder untuk entri tanpa EstimatedSize dimuat di latar belakang setelah daftar tampil.
   Tes integrasi pemindai: `dotnet test --filter Category=Integration` (hanya baca registri/paket mesin ini).
3. [x] Hapus instalan standar: `UninstallCommandBuilder` (EXE/MSI `/x`/Inno senyap/Store), `UninstallRunner` (Core),
   `JobProcessRunner` (CreateProcess suspended + Job Object, tunggu proses anak, batas 30 menit, Hentikan =
   TerminateJobObject), verifikasi Berhasil/Sebagian/Gagal, dialog 5 mode (mode terakhir diingat), cek program
   yang sedang berjalan (tutup paksa hanya proses di folder program; proses kritis ditolak), Mode Pemburu
   (seret target ke jendela/ikon desktop/baki, atau jatuhkan .lnk/.exe ke jendela utama).
   **Hapus paksa** baru menampilkan pratinjau target; eksekusinya menunggu karantina di Tahap 4.
   Mode Aman/Normal/Super saat ini sama dengan Bawaan (pemindaian sisa di Tahap 5).
4. [x] Keamanan: `ProtectedPaths` + `ProtectedRegistryKeys` (Core/Safety), `SafeRemover` (satu-satunya jalur hapus:
   cek lindung → jurnal Pending → karantina/ekspor .reg → hapus → jurnal Done), `QuarantineService` (pindah;
   antar-drive salin lengkap dulu; salinan TIDAK PERNAH dibuang setelah sumber disentuh; berkas terkunci →
   MoveFileEx saat mulai ulang; junction dilewati), `RegistryBackupService` (reg.exe export/import /reg:64),
   `RestorePointService` (WMI, Throttled bila dibatasi 24 jam — pengaturan Windows tidak diubah),
   `SqliteSessionJournal` (riwayat.db, WAL), `SessionRestorer` (Batalkan per sesi, urutan terbalik).
   Terhubung ke UI: titik pemulihan sekali per sesi, semua aksi dijurnal, Hapus paksa sungguhan (konfirmasi kedua),
   "Hapus entri rusak" (hanya bila uninstaller tidak ada), tawaran pemulihan sesi terputus saat aplikasi dibuka,
   karantina kedaluwarsa dikosongkan otomatis. Tes integrasi memakai folder sementara dan kunci
   `HKCU\Software\MyUninstallerTests\<guid>` (dibuat dan dihapus tes sendiri).
   Menu Riwayat (lihat/Batalkan sesi lama) ada di Tahap 6.
5. [x] Pemindai sisa + skor keyakinan. Core/Leftovers: `NameMatcher` (normalisasi nama, nama umum diabaikan),
   `LeftoverContext` (semua aturan "jangan ambil milik program lain": lokasi program lain, nama sama dengan program
   lain, penerbit yang masih dipakai), `ConfidenceScorer` (Aman ≥80 dicentang, Mungkin 50–79, Ragu <50 tersembunyi).
   `LeftoverScanner` (Infrastructure, lokasi via `LeftoverScanRoots` agar bisa dites): Normal = folder, pintasan,
   SOFTWARE, kunci Uninstall, App Paths, Run; Super = + layanan, tugas terjadwal (XML di System32\Tasks), CLSID,
   pencarian nama di semua drive. `SafeRemover` kini juga menangani nilai registri, layanan (bukan driver/bawaan
   Windows), dan tugas terjadwal (bukan \Microsoft\), semuanya dengan cadangan. Layar hasil `LeftoverResultsWindow`
   (per program → Aman/Mungkin/Ragu, konfirmasi kedua untuk yang tidak Aman, ringkasan + Batalkan).
   Mode Aman membersihkan kelompok Aman otomatis. Firewall belum dipindai (tidak ada cara aman cadang/pulihkan per aturan).
   Pelajaran dari uji baca-saja di mesin nyata (jangan diulang): JANGAN cocokkan "kata pertama" nama program
   ("Android Studio" → folder SDK "Android", "GitHub Desktop" → folder repositori "GitHub"); "mirip" hanya bila nama
   kandidat MEMUAT nama program; Dokumen/profil/pencarian drive hanya nama persis dan selalu Ragu; folder bertitik
   (`.git`) dan tersembunyi dilewati pada pencarian drive.
6. [x] Batalkan & riwayat: menu Riwayat (`HistoryWindow` + `HistoryViewModel`, dibuka dari panel kiri) berisi daftar
   sesi (status Selesai/Terputus/Dibatalkan), rincian aksi, "Batalkan sesi" (`SessionRestorer`), "Salin laporan"
   (`HistoryReport.BuildText`), "Buka folder karantina", dan "Kosongkan karantina sesi" (hapus permanen, konfirmasi,
   tombol merah; ID sesi divalidasi agar tidak keluar dari folder karantina). `HistoryReport` di Core menghitung
   ringkasan dan item yang masih bisa dipulihkan (cadangannya harus masih ada). Keterangan aksi di jurnal ditulis
   dalam bahasa Indonesia (kunci `RestorePointResult_*`, `OutcomeShort_*`). Uji penerimaan PRD "Batalkan
   mengembalikan semua berkas dan registri satu sesi" ada di `HistoryIntegrationTests` (karantina, reg.exe, SQLite
   sungguhan pada folder sementara + kunci HKCU uji).
   Pemeriksaan tampilan: render jendela di luar layar ke PNG (RenderTargetBitmap) — JANGAN tangkap layar desktop
   pengguna (bisa merekam jendela lain yang sedang dibuka).
7. [x] Rilis 1.0 (versi 1.0.0 di `Directory.Build.props`). `build-rilis.ps1` = tes Release → publish profil
   `Rilis` (`artifacts\publish\MyUninstaller.exe`, single-file, ReadyToRun, ±32 MB) → ISCC
   `installer\MyUninstaller.iss` (`artifacts\installer\MyUninstaller-<versi>-setup.exe`, lzma2; dilewati bila Inno
   Setup 6 belum terpasang).
   **Keputusan pengguna (15 November 2026): framework-dependent, bukan self-contained seperti PRD**, karena
   self-contained ±67 MB (installer ±61 MB) jauh di atas target PRD 15 MB (WPF tidak bisa di-trim; proyeksi WinRT
   ±25 MB). Installer memeriksa .NET 10 Desktop Runtime x64 dan memberi tautan unduh bila belum ada; AppMutex
   menutup aplikasi sebelum memperbarui; saat uninstall, data `%ProgramData%\MyUninstaller` (riwayat, log,
   karantina) hanya dihapus bila pengguna setuju (bawaan: disimpan). GUID `AppId` installer jangan diubah.
   Waktu mulai (Release, laptop ini): kode aplikasi → daftar program ±1,8 detik; dari klik ganda ±2,6–3,3 detik.
   Pemindaian pertama dimulai sejajar dengan pembuatan jendela (`InitialScan`).
   Belum: uji di VM Windows 10 22H2/11 24H2 (kriteria penerimaan PRD), pengecualian Windows Defender tidak
   dipasang otomatis (mengubah pengaturan keamanan — lakukan manual bila perlu).
8. Versi 1.1–1.3 (berjalan):
   - [x] 1.1 Uninstall massal: antrean/sisa gabungan sudah ada sejak Tahap 3/5; ditambah **mode senyap** (kotak di
     dialog mode, bawaan tercentang bila >1 program, menyebut "n dari m program" yang mendukung).
     `InstallerKindDetector` mengenali NSIS (`/S`) dan Inno (`/VERYSILENT ...`) dari penanda di isi exe (hanya baca,
     maks 8 MB); urutan: QuietUninstallString → MSI `/qn` → Inno (unins000.exe) → deteksi isi. Program tanpa cara
     senyap terdokumentasi tetap interaktif (TIDAK menebak parameter).
     **Peringatan keras** (`ProgramRiskRules`, aturan PRD 1.0 yang sempat terlewat): driver, runtime bersama
     (.NET, VC++, WebView2, ...), komponen sistem → dialog Batal (bawaan) / Lewati yang berisiko / Tetap hapus (merah).
     Contoh nyata di laptop pengembang: "Windows Driver Package - ASUS (AsusPTPDrv)" = driver touchpad.
   - [ ] 1.2 Program awal (Run, folder Startup, Task Scheduler) + penanda "jarang dipakai" (Prefetch/UserAssist).
   - [ ] 1.3 Ekstensi browser (Chrome/Edge/Firefox).

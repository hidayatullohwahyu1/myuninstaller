# PRD My Uninstaller! — Aplikasi Uninstaller Windows

Nov 14, 2026 · @Rappiijiahan

## Ringkasan

My Uninstaller! adalah aplikasi uninstaller Windows 10/11 berbahasa Indonesia, dibangun dengan C# .NET 10 + WPF. Aplikasi ini menghapus program sampai bersih (file, folder, registry, layanan, tugas terjadwal) dengan aman, lengkap dengan cadangan dan tombol batalkan.

Masalah yang diselesaikan:

- Uninstaller bawaan program sering meninggalkan sisa file dan registry yang menumpuk.
- Your Uninstaller! sudah lama tidak diperbarui dan kurang mendukung aplikasi Store/UWP di Windows 10/11.
- Alat sejenis (Revo, IObit) berbayar, berbahasa Inggris, atau menyisipkan iklan dan bloatware.

Nama aplikasi: **My Uninstaller!** (untuk pemakaian pribadi). Nama teknis untuk folder, proyek, dan kode: `MyUninstaller`.

## Tujuan, pengguna, dan batasan

Tujuan utama: menghapus program lebih bersih daripada fitur bawaan Windows, tanpa pernah merusak sistem.

Sasaran pengguna:

- Dipakai sendiri di laptop pribadi dan komputer kerja; tidak didistribusikan ke publik.
- Pengguna berpengalaman yang sering memasang dan menghapus program, jadi alur harus cepat dan minim klik.

Target terukur:

- Daftar program tampil kurang dari 2 detik pada PC rata-rata.
- Ukuran installer kurang dari 15 MB.
- Nol kasus Windows gagal boot akibat penghapusan (semua aksi bisa dibatalkan).

Batasan:

- Hanya Windows 10 (1809+) dan Windows 11, 64-bit.
- Butuh hak Administrator untuk hapus sisa di HKLM dan Program Files.
- Tidak menghapus komponen sistem Windows, driver, atau runtime yang dipakai banyak program (misalnya .NET, VC++ Redistributable) tanpa peringatan keras.

## Daftar fitur

Versi 1.0 (MVP) fokus pada daftar program, uninstall, dan pembersihan sisa yang aman. Fitur lain menyusul di versi 1.x dan 2.0.

| Fitur | Keterangan | Versi |
| --- | --- | --- |
| Daftar program Win32 | Baca registry Uninstall (HKLM 64/32-bit, HKCU): nama, penerbit, versi, ukuran, tanggal pasang, ikon | 1.0 |
| Daftar aplikasi Store/UWP | Lewat PackageManager (Windows.Management.Deployment) | 1.0 |
| Cari, saring, urutkan | Berdasarkan nama, penerbit, ukuran, tanggal | 1.0 |
| Uninstall standar | Mode Bawaan, Aman, Normal, Super (lihat Alur kerja uninstall) | 1.0 |
| Pindai sisa | File, folder, registry, shortcut setelah uninstall, dengan skor keyakinan | 1.0 |
| Cadangan + Batalkan | Restore point, ekspor registry .reg, file ke karantina | 1.0 |
| Riwayat penghapusan | Log semua aksi, bisa dipulihkan dari karantina | 1.0 |
| Uninstall massal | Centang banyak program, jalankan berurutan, mode senyap bila tersedia | 1.1 |
| Paksa uninstall | Untuk program yang uninstaller-nya rusak/hilang | 1.0 |
| Mode Pemburu | Seret ikon target ke jendela, shortcut, atau ikon tray | 1.0 |
| Startup manager | Lihat/nonaktifkan program startup (Run, Startup folder, Task Scheduler) | 1.2 |
| Penanda "jarang dipakai" | Perkiraan terakhir dipakai dari Prefetch/UserAssist | 1.2 |
| Ekstensi browser | Daftar dan hapus ekstensi Chrome/Edge/Firefox | 1.3 |
| Pantau instalasi | Rekam snapshot sebelum/sesudah instal agar bisa dihapus 100% | 2.0 |
| Program portable | Deteksi folder program tanpa installer | 2.0 |
| Integrasi winget | Update dan pasang ulang program | 2.0 |
| Menu klik kanan | "Hapus instalan dengan My Uninstaller!" di Explorer | 2.0 |

## Tech stack dan arsitektur

Aplikasi memakai pola MVVM dengan tiga lapisan: UI (WPF), Core (logika uninstall dan pemindaian), dan Infrastruktur (akses registry, file, Windows API).

| Komponen | Pilihan |
| --- | --- |
| Bahasa / runtime | C# 14, .NET 10 LTS (didukung sampai November 2028; .NET 8 sudah habis masa dukungannya November 2026), target net10.0-windows10.0.19041 |
| UI | WPF dengan tema kustom bergaya Your Uninstaller! (panel kiri, grid ikon besar, panel info bawah), ikon Segoe Fluent, mode terang/gelap |
| MVVM | CommunityToolkit.Mvvm |
| Dependency injection | Microsoft.Extensions.Hosting |
| Registry & file | Microsoft.Win32.Registry, System.IO |
| Aplikasi Store | Windows.Management.Deployment.PackageManager (WinRT) |
| Restore point | WMI SystemRestore (System.Management) |
| Penyimpanan riwayat | SQLite (Microsoft.Data.Sqlite) di %ProgramData%\\MyUninstaller |
| Log | Serilog ke file harian |
| Tes | xUnit + FluentAssertions |
| Paket rilis | Publish self-contained single-file, installer Inno Setup |

Alur utama: Pemindai Program → daftar di UI → pengguna pilih → Mesin Cadangan (restore point + backup) → Pelaksana Uninstall → Pemindai Sisa → pengguna centang sisa → Penghapus (pindah ke karantina) → catat di Riwayat.

Aplikasi berjalan dengan manifest `requireAdministrator` supaya semua lokasi bisa dibaca dan dihapus.

## Detail teknis

Bagian ini adalah inti aplikasi: dari mana daftar program diambil, bagaimana uninstall dijalankan, dan bagaimana sisa ditemukan tanpa salah hapus.

### Sumber daftar program

1. `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall` (64-bit).
2. `HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall` (32-bit).
3. `HKCU\...\Uninstall` untuk program per-pengguna.
4. Paket Store/UWP lewat PackageManager.FindPackagesForUser.

Aturan saring: sembunyikan entri `SystemComponent=1`, entri tanpa DisplayName, dan update Windows (`ParentKeyName`, `ReleaseType`), kecuali opsi "Tampilkan komponen sistem" dinyalakan. Jika `EstimatedSize` kosong, hitung ukuran dari `InstallLocation`.

### Menjalankan uninstall

- Pakai `QuietUninstallString` bila mode senyap dipilih dan tersedia; jika tidak, `UninstallString`.
- Untuk MSI (`MsiExec.exe /I{GUID}`), ubah menjadi `msiexec /x {GUID}` (+ `/qn` untuk senyap).
- Tunggu proses dan semua proses anaknya selesai (Job Object), batas waktu 30 menit.
- Paket UWP: PackageManager.RemovePackageAsync.

### Pemindaian sisa

Setelah uninstall selesai, cari kecocokan nama program, penerbit, dan nama folder instalasi di:

- Folder: `InstallLocation`, Program Files (x86/x64), ProgramData, AppData Roaming/Local/LocalLow, Start Menu, Desktop.
- Registry: `HKCU/HKLM\SOFTWARE\<Penerbit>\<Program>`, kunci Uninstall yang tertinggal, CLSID/AppID yang menunjuk ke folder program, asosiasi file.
- Layanan Windows dan tugas terjadwal yang path exe-nya ke folder program.
- Entri startup (Run/RunOnce) dan aturan Firewall untuk exe program.

### Skor keyakinan

Setiap temuan diberi skor 0–100 lalu dikelompokkan:

| Kelompok | Skor | Contoh | Bawaan |
| --- | --- | --- | --- |
| Aman | 80–100 | Folder `InstallLocation` yang tersisa, kunci registry persis nama program | Dicentang |
| Mungkin | 50–79 | Folder AppData bernama penerbit saja | Tidak dicentang |
| Ragu | di bawah 50 | Nama mirip tapi tidak persis | Disembunyikan, bisa dibuka |

Daftar lindung (tidak pernah dihapus): `C:\Windows`, `System32`, folder bersama seperti `Common Files` dan `Microsoft`, kunci registry milik Microsoft dan kunci induk penerbit yang masih dipakai program lain dari penerbit sama.

## Keamanan, cadangan, dan batalkan

Setiap penghapusan harus bisa dipulihkan. Tidak ada file atau registry yang dihapus permanen pada langkah pertama.

- **Restore point**: dibuat otomatis sebelum sesi uninstall (bisa dimatikan di Pengaturan). Jika System Restore nonaktif, tampilkan peringatan.
- **Backup registry**: setiap kunci yang akan dihapus diekspor ke file .reg di folder sesi.
- **Karantina file**: file dan folder dipindah ke `%ProgramData%\MyUninstaller\Karantina\<tanggal-sesi>`, bukan dihapus. Karantina dikosongkan otomatis setelah 30 hari (bisa diatur).
- **Tombol Batalkan**: di menu Riwayat, satu klik mengembalikan file dari karantina dan mengimpor ulang .reg.
- **File terkunci**: jika file sedang dipakai, tawarkan hapus saat restart (MoveFileEx dengan MOVEFILE\_DELAY\_UNTIL\_REBOOT).
- **Konfirmasi ganda**: penghapusan di luar folder program atau skor di bawah 50 membutuhkan konfirmasi kedua.
- **Tanpa internet**: aplikasi tidak mengirim data apa pun; tidak ada iklan, telemetri, atau bundling program lain.

## Desain UI/UX

Tata letak meniru Your Uninstaller! 7: panel tugas di kiri, grid ikon besar di tengah, panel info program di bawah. Tampilannya dibuat modern: tajam di layar high-DPI, ada mode gelap, dan animasi halus. Ikon, logo, dan nama dibuat sendiri; tidak memakai aset milik URSoft.

### Jendela utama

```
+--------------------------------------------------------------------+
| [ikon] My Uninstaller!                    [ Cari program...  ] [⟳] |
+--------------+-----------------------------------------------------+
| PROGRAM      |  [ikon]   [ikon]   [ikon]   [ikon]   [ikon]   [ikon] |
|  Hapus       |  7-Zip    Chrome   VLC      WinRAR   Zoom     ...    |
|   instalan   |                                                     |
|  Hapus paksa |  [ikon]   [ikon]   [ikon]   [ikon]   [ikon]   [ikon] |
|  Mode Pemburu|  ...      ...  BARU ...      ...  JARANG  ...       |
|  Perbaikan   |                                                     |
|   otomatis   |                                                     |
|  Riwayat     |                                                     |
| ALAT         +-----------------------------------------------------+
|  Program awal| [ikon besar] Google Chrome 131.0  [ Hapus instalan ]|
|  Pembersih   | Google LLC · 412 MB · dipasang 2 September 2026     |
| TAMPILAN     | C:\Program Files\Google\Chrome · situs penerbit      |
|  Ikon/Rincian|                                                     |
+--------------+-----------------------------------------------------+
| 142 program · total 38,2 GB · 1 dipilih                            |
+--------------------------------------------------------------------+
```

- **Kepala jendela**: ikon, nama aplikasi, kotak cari besar (langsung menyaring saat mengetik), tombol Segarkan.
- **Panel kiri (200 px)**: grup Program (Hapus instalan, Hapus paksa, Mode Pemburu, Perbaikan otomatis, Riwayat), grup Alat (Program awal, Pembersih — versi 1.2+), grup Tampilan (Ikon besar / Rincian, urutkan).
- **Area utama**: bawaannya kisi ikon 48 px dengan nama di bawahnya, seperti Your Uninstaller!. Bisa diganti ke tampilan Rincian (tabel berkolom). Label kecil "BARU" (dipasang 7 hari terakhir) dan "JARANG" (tidak dipakai 90 hari).
- **Panel info bawah**: ikon besar, nama, versi, penerbit, ukuran, tanggal pasang, lokasi, tautan situs, dan tombol besar Hapus instalan.
- **Bilah status**: jumlah program, total ukuran, jumlah yang dipilih.
- **Menu klik kanan**: Hapus instalan, Hapus paksa, Buka lokasi folder, Buka di Editor Registri, Cari di Google, Salin nama, Hapus entri rusak.
- **Pintasan**: Del = Hapus instalan, Ctrl+F = Cari, F5 = Segarkan, Ctrl+H = Mode Pemburu, Ctrl+A = Pilih semua.

Ukuran jendela minimal 900×600 px, dan posisi serta ukuran terakhir diingat. Huruf Segoe UI Variable. Tombol berbahaya berwarna merah dan tidak pernah jadi tombol bawaan Enter.

## Alur kerja uninstall

Satu program dihapus bersih dalam 3 klik: pilih → Uninstall → Hapus sisa. Langkah lain berjalan otomatis di satu jendela proses yang ringkas.

### Mode uninstall

Seperti Your Uninstaller!, pengguna memilih mode di dialog kecil. Pilihan terakhir diingat, jadi cukup tekan Enter.

| Mode | Yang dilakukan | Kapan dipakai |
| --- | --- | --- |
| Bawaan | Hanya menjalankan uninstaller program | Program tepercaya, ingin cepat |
| Aman | Bawaan + hapus otomatis sisa berlabel Aman saja | Pemakaian harian tanpa repot |
| Normal (bawaan) | Bawaan + tampilkan sisa Aman dan Mungkin untuk dicentang | Pilihan seimbang |
| Super | Normal + pemindaian mendalam (CLSID, layanan, tugas terjadwal, firewall, pencarian nama di seluruh drive) | Program bandel atau sisa banyak |
| Hapus paksa | Lewati uninstaller bawaan, langsung hapus file dan registry (paksa) | Uninstaller rusak atau hilang |

### Langkah per sesi

1. Pilih satu atau banyak program, lalu tekan Hapus instalan (atau Del).
2. Pilih mode di dialog, lalu Enter.
3. Pemeriksaan awal otomatis: jika program sedang berjalan, tawarkan tutup paksa. Buat restore point dan catat kondisi awal.
4. Jalankan uninstaller sambil menampilkan progres. Jika uninstaller menunggu input lebih dari 2 menit, tampilkan pemberitahuan.
5. Verifikasi: cek apakah entri registry program sudah hilang. Status: Berhasil / Sebagian / Gagal.
6. Pindai sisa sesuai mode (target di bawah 10 detik untuk mode Normal).
7. Layar hasil: pohon temuan yang bisa dicentang, lalu klik "Hapus yang dicentang" atau "Lewati".
8. Ringkasan: jumlah file dan kunci registry yang dibersihkan, ruang yang dibebaskan, tombol Batalkan.

Uninstall massal: dialog mode cukup sekali. Program masuk antrean dan dijalankan senyap satu per satu. Sisa dari semua program digabung di satu layar hasil di akhir, dan item yang gagal tidak menghentikan antrean.

### Ketangguhan (robust)

- **Jurnal sesi**: setiap aksi dicatat dulu ke SQLite sebelum dijalankan. Jika aplikasi crash atau listrik padam, saat dibuka lagi aplikasi menawarkan Lanjutkan atau Pulihkan.
- **Tidak membeku**: semua pemindaian dan penghapusan berjalan async di latar belakang dan bisa dihentikan kapan saja.
- **Restore point**: Windows biasanya hanya membuat satu restore point per 24 jam. Jika ditolak, aplikasi tetap lanjut dengan backup .reg dan karantina, lalu memberi tahu pengguna.
- **Satu instance**: aplikasi hanya bisa dibuka satu kali (mutex), agar dua sesi tidak saling bentrok.
- **Laporan**: log harian Serilog dan tombol "Salin laporan" untuk melacak masalah.
- **Daftar lindung** selalu dicek ulang tepat sebelum menghapus, bukan hanya saat memindai.

## Bahasa antarmuka (sesuai KBBI)

Semua teks antarmuka memakai bahasa Indonesia baku sesuai KBBI dan EYD Edisi V. Nama aplikasi "My Uninstaller!" tetap dalam bahasa Inggris karena nama diri.

Aturan penulisan:

- Label tombol dan menu memakai huruf kapital hanya di awal (contoh: "Hapus instalan", bukan "Hapus Instalan"). Judul grup panel kiri memakai huruf kapital semua.
- Kalimat pesan pendek, aktif, dan sopan tanpa kata "Anda" berulang. Contoh: "Program berhasil dihapus. 12 berkas sisa dibersihkan."
- Angka desimal memakai koma (38,2 GB), ribuan memakai titik (1.250 berkas), tanggal "2 September 2026", jam "14.30".
- Istilah asing yang belum ada padanannya ditulis miring di dokumentasi, misalnya *startup* hanya bila perlu; di antarmuka pakai padanan di bawah.

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

Semua teks disimpan di satu berkas sumber daya `Strings.id.resx`, sehingga mudah diperiksa dan tidak ada teks Inggris yang tercecer di kode. Daftar istilah ini juga disalin ke CLAUDE.md.

## Kompatibilitas dan stabilitas Windows 10/11

Aplikasi tidak boleh membuat Windows rusak, gagal boot, atau aplikasi sendiri crash. Aturan di bawah wajib dan dicek di setiap tahap.

### Kompatibilitas

- Diuji di Windows 10 22H2 dan Windows 11 24H2/25H2, 64-bit.
- Dipublikasikan *self-contained* single-file, jadi tidak perlu memasang runtime .NET.
- Manifest: `requireAdministrator`, `PerMonitorV2` (tajam di layar 4K dan skala 125–200%), `longPathAware`, GUID supportedOS Windows 10/11.
- Registri dibaca eksplisit dengan `RegistryView.Registry64` dan `Registry32`, supaya tidak tertukar oleh pengalihan WOW64.
- Mengikuti tema terang/gelap dan warna aksen Windows; ikon program di Explorer diperbarui lewat `SHChangeNotify` tanpa mematikan explorer.exe.

### Aturan agar sistem tidak rusak

- **Tidak pernah disentuh**: `C:\Windows` (termasuk System32 dan WinSxS), `HKLM\SYSTEM\CurrentControlSet\Control`, kunci milik TrustedInstaller, dan layanan/driver bawaan Microsoft. Jika akses ditolak, item dilewati; aplikasi tidak mengambil alih kepemilikan.
- **Berkas bersama**: cek penghitung `SharedDLLs` dan referensi komponen Windows Installer; berkas yang masih dipakai program lain tidak dihapus.
- **Proses kritis** (csrss, wininit, winlogon, lsass, services, svchost) tidak pernah dihentikan. explorer.exe hanya dimulai ulang dengan persetujuan pengguna.
- **Layanan dan driver** pihak ketiga dihentikan dulu, baru dihapus. Driver kernel hanya di mode Super dengan konfirmasi kedua dan wajib mulai ulang komputer.
- **Tidak ada "pembersih registri" umum**: hanya kunci yang terkait jelas dengan program yang dihapus.
- Aplikasi sendiri tidak memasang driver, tidak menyuntik kode ke proses lain, dan tidak mengubah berkas sistem.

### Aturan agar aplikasi tidak crash

- Penangan galat global (`DispatcherUnhandledException`, `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`): galat dicatat, pesan ramah ditampilkan, dan jurnal sesi menjaga agar tidak ada aksi setengah jalan.
- Setiap item dibungkus try/catch sendiri; satu kegagalan tidak menghentikan pemindaian atau antrean.
- Batas waktu untuk setiap proses luar, dan ikon/entri registri yang rusak ditampilkan dengan ikon bawaan, bukan galat.

### Uji keamanan sistem

Setiap rilis diuji di mesin virtual dengan *snapshot*: hapus 20 program umum, lalu jalankan `sfc /scannow` dan `DISM /Online /Cleanup-Image /ScanHealth`, mulai ulang komputer, dan periksa Event Viewer. Rilis hanya lolos bila tidak ada pelanggaran integritas, Windows berjalan normal, dan tidak ada galat baru.

## Ikon aplikasi

Ikon dibuat orisinal: monogram **MU** (singkatan My Uninstaller!) di atas ubin biru membulat bergaya Windows 11. Huruf M berwarna putih, huruf U berwarna jingga, dan di dalam cekungan U ada panah putih ke atas sebagai tanda program dikeluarkan bersih. Kilau kecil di pojok kanan atas menandakan hasilnya bersih.

Berkas: `MyUninstaller.ico` (ukuran 16, 20, 24, 32, 40, 48, 64, 128, dan 256 px dalam satu berkas) disimpan di `src/MyUninstaller.App/Assets/`, dipasang lewat `<ApplicationIcon>` di .csproj dan juga dipakai untuk ikon jendela dan installer. Versi kecil (16–24 px) disederhanakan tanpa panah dan kilau, dengan huruf lebih tebal, supaya tetap jelas di bilah tugas.

## Struktur proyek

Satu solution dengan empat project, supaya logika inti bisa dites tanpa UI.

```
MyUninstaller.sln
├─ src/
│  ├─ MyUninstaller.App/            # WPF: Views, ViewModels, App.xaml, app.manifest, Assets/MyUninstaller.ico
│  ├─ MyUninstaller.Core/           # Model, interface, logika hapus instalan & skor sisa
│  │   ├─ Models/                   # InstalledProgram, LeftoverItem, UninstallSession
│  │   ├─ Scanning/                 # ProgramScanner, LeftoverScanner, ConfidenceScorer
│  │   ├─ Uninstall/                # UninstallRunner, MsiHelper, UwpRemover
│  │   └─ Safety/                   # ProtectedPaths, BackupService, QuarantineService, SessionJournal
│  └─ MyUninstaller.Infrastructure/ # Registry, FileSystem, WMI, SQLite, Win32 P/Invoke
├─ tests/
│  └─ MyUninstaller.Tests/          # xUnit: scorer, filter, MSI parsing, protected paths
├─ installer/                       # skrip Inno Setup
└─ CLAUDE.md                        # aturan proyek untuk Claude Code
```

Isi CLAUDE.md minimal: tujuan aplikasi, aturan "jangan pernah hapus permanen tanpa karantina", daftar path terlindung, perintah build (`dotnet build`, `dotnet test`), dan gaya kode.

## Tahapan pengerjaan dengan Claude Code

Kerjakan satu tahap per sesi Claude Code; setiap tahap harus lolos `dotnet build` dan `dotnet test` sebelum lanjut. Uji di mesin virtual (Hyper-V/VirtualBox) dengan snapshot, jangan di laptop kerja.

1. **Kerangka proyek**: buat solution 4 project, DI, Serilog, CLAUDE.md, manifest admin, kerangka jendela utama bergaya Your Uninstaller! (panel kiri, grid ikon, panel info, status bar).
2. **Pemindai program**: baca 3 lokasi registry + UWP, filter komponen sistem, tampilkan di tabel dengan cari/urut.
3. **Uninstall standar**: UninstallRunner (EXE, MSI, senyap), tunggu proses anak, UWP remover, dialog 4 mode, Hapus paksa, dan Mode Pemburu.
4. **Keamanan dulu**: ProtectedPaths, BackupService (restore point + .reg), QuarantineService, tabel Riwayat SQLite.
5. **Pemindai sisa + skor**: LeftoverScanner, ConfidenceScorer, layar hasil dengan centang dan warna.
6. **Batalkan & riwayat**: pulihkan file dan registry per sesi.
7. **Rilis 1.0**: publish single-file, installer Inno Setup, uji di Windows 10 dan 11.
8. **Versi 1.1–1.3**: uninstall massal, startup manager, ekstensi browser.
9. **Versi 2.0**: pantau instalasi, program portable, winget, menu klik kanan.

Contoh perintah awal untuk Claude Code: "Baca PRD ini, lalu kerjakan Tahap 1 saja. Tanyakan dulu jika ada yang tidak jelas."

## Kriteria penerimaan dan pengujian

Versi 1.0 dianggap selesai jika semua poin di bawah lolos di Windows 10 22H2 dan Windows 11 24H2.

- [ ] Daftar program cocok dengan "Aplikasi terinstal" di Pengaturan Windows (selisih hanya komponen sistem yang disembunyikan).
- [ ] Uninstall berhasil untuk program uji: 7-Zip (MSI dan EXE), VLC, Notepad++, Google Chrome, WinRAR, satu aplikasi Store.
- [ ] Setelah uninstall, pemindai sisa menemukan folder dan kunci registry yang tertinggal dan memberi skor "Aman".
- [ ] Tidak satu pun path di daftar lindung muncul sebagai temuan.
- [ ] Batalkan mengembalikan semua file dan registry dari satu sesi; program yang dipulihkan tetap tercatat di registry.
- [ ] Restore point muncul di System Restore Windows dengan nama "My Uninstaller! - \<nama program>".
- [ ] Unit test lolos untuk ConfidenceScorer, filter komponen sistem, konversi MSI, dan ProtectedPaths.
- [ ] Aplikasi tetap responsif (UI tidak membeku) selama pemindaian.

## Risiko dan mitigasi

| Risiko | Dampak | Mitigasi |
| --- | --- | --- |
| Salah hapus file/registry milik program lain | Program lain rusak | Skor keyakinan, daftar lindung, karantina + Batalkan |
| Uninstaller pihak ketiga macet atau minta interaksi | Proses menggantung | Batas waktu, tombol Hentikan, deteksi jendela dialog |
| Antivirus menandai aplikasi sebagai mencurigakan | Pengguna ragu memasang | Tambahkan pengecualian folder aplikasi di Windows Defender |
| System Restore nonaktif di PC pengguna | Tidak ada pemulihan sistem penuh | Peringatan jelas; tetap andalkan backup .reg dan karantina |
| Perubahan API Windows di masa depan | Fitur UWP/startup error | Bungkus akses Windows di lapisan Infrastruktur agar mudah diganti |

Keputusan: aplikasi dipakai sendiri, tidak dibagikan ke publik. Jadi tidak perlu code signing, installer publik, maupun halaman unduh; cukup exe single-file atau installer Inno Setup untuk komputer sendiri.

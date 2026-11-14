using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MyUninstaller.App.Localization;
using MyUninstaller.Core.Formatting;
using MyUninstaller.Core.Models;

namespace MyUninstaller.App.ViewModels;

/// <summary>Satu program di kisi ikon / tabel rincian.</summary>
public sealed partial class ProgramItemViewModel : ObservableObject
{
    // Warna ubin pengganti ikon, dipilih tetap berdasarkan nama.
    private static readonly SolidColorBrush[] TileBrushes = CreateTileBrushes(
        "#2563EB", "#7C3AED", "#DB2777", "#EA580C", "#059669", "#0891B2", "#4F46E5", "#B45309");

    public ProgramItemViewModel(InstalledProgram model, DateOnly today)
    {
        Model = model;
        IsNew = model.IsNew(today);
        IsRarelyUsed = model.IsRarelyUsed(today);
        TileBrush = TileBrushes[StableHash(model.DisplayName) % (uint)TileBrushes.Length];
        Initial = model.DisplayName.Trim() is { Length: > 0 } name ? char.ToUpperInvariant(name[0]).ToString() : "?";
    }

    /// <summary>Data program; diganti saat ukuran selesai dihitung dari folder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeBytes), nameof(SizeText), nameof(SummaryText))]
    public partial InstalledProgram Model { get; private set; }

    /// <summary>Menerapkan ukuran hasil hitung folder (EstimatedSize kosong).</summary>
    public void ApplyComputedSize(long bytes) => Model = Model with { SizeBytes = bytes, IsSizeComputed = true };

    /// <summary>Asal program, misalnya "Program 64-bit · Windows Installer".</summary>
    public string SourceText
    {
        get
        {
            string source = Strings.Get(Model.Source switch
            {
                ProgramSource.Registry64 => "Source_Registry64",
                ProgramSource.Registry32 => "Source_Registry32",
                ProgramSource.CurrentUser => "Source_CurrentUser",
                _ => "Source_Store",
            });
            if (Model.IsWindowsInstaller)
            {
                source += " · " + Strings.Get("Source_Msi");
            }

            if (Model.IsSystemComponent)
            {
                source += " · " + Strings.Get("Source_SystemComponent");
            }

            return source;
        }
    }

    public string DisplayName => Model.DisplayName;

    public string PublisherText => string.IsNullOrWhiteSpace(Model.Publisher) ? Strings.Get("Info_UnknownPublisher") : Model.Publisher;

    public string VersionText => Model.Version ?? string.Empty;

    /// <summary>Nama dan versi untuk judul panel info, tanpa mengulang versi yang sudah ada di nama.</summary>
    public string TitleText =>
        string.IsNullOrWhiteSpace(Model.Version) || Model.DisplayName.Contains(Model.Version, StringComparison.OrdinalIgnoreCase)
            ? Model.DisplayName
            : $"{Model.DisplayName} {Model.Version}";

    public long SizeBytes => Model.SizeBytes ?? 0;

    public string SizeText => Model.SizeBytes is { } size ? IndonesianFormat.Size(size) : string.Empty;

    public string InstallDateText => Model.InstallDate is { } date ? IndonesianFormat.Date(date) : string.Empty;

    /// <summary>Contoh: "Google LLC · 412 MB · dipasang 2 September 2026".</summary>
    public string SummaryText
    {
        get
        {
            var parts = new List<string>(3) { PublisherText };
            parts.Add(Model.SizeBytes is { } size ? IndonesianFormat.Size(size) : Strings.Get("Info_UnknownSize"));
            if (Model.InstallDate is { } date)
            {
                parts.Add(Strings.Format("Info_Installed", IndonesianFormat.Date(date)));
            }

            if (Model.LastUsed is { } lastUsed)
            {
                parts.Add(Strings.Format("Info_LastUsed", IndonesianFormat.Date(lastUsed)));
            }

            return string.Join(" · ", parts);
        }
    }

    public bool HasLocation => !string.IsNullOrWhiteSpace(Model.InstallLocation);

    public string LocationText => HasLocation ? Model.InstallLocation! : Strings.Get("Info_UnknownLocation");

    public bool HasWebsite => !string.IsNullOrWhiteSpace(Model.WebsiteUrl);

    public bool IsNew { get; }

    /// <summary>Label JARANG: terakhir dipakai 90 hari lalu atau lebih (hanya bila ada bukti pemakaian).</summary>
    [ObservableProperty]
    public partial bool IsRarelyUsed { get; private set; }

    /// <summary>Menerapkan perkiraan terakhir dipakai dari Prefetch/UserAssist (versi 1.2).</summary>
    public void ApplyLastUsed(DateOnly lastUsed, DateOnly today)
    {
        Model = Model with { LastUsed = lastUsed };
        IsRarelyUsed = Model.IsRarelyUsed(today);
    }

    /// <summary>Ikon program; Tahap 2 mengisi dari DisplayIcon. Null berarti pakai ubin pengganti.</summary>
    [ObservableProperty]
    public partial ImageSource? Icon { get; set; }

    public bool HasIcon => Icon is not null;

    public Brush TileBrush { get; }

    public string Initial { get; }

    partial void OnIconChanged(ImageSource? value) => OnPropertyChanged(nameof(HasIcon));

    /// <summary>Hash FNV-1a agar warna ubin sama di setiap kali aplikasi dibuka.</summary>
    private static uint StableHash(string text)
    {
        uint hash = 2166136261;
        foreach (char c in text)
        {
            hash = (hash ^ char.ToLowerInvariant(c)) * 16777619;
        }

        return hash;
    }

    private static SolidColorBrush[] CreateTileBrushes(params string[] colors) =>
        [.. colors.Select(hex =>
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        })];
}

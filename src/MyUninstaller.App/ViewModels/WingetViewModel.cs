using System.Collections.ObjectModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyUninstaller.App.Localization;
using MyUninstaller.Core.Formatting;
using MyUninstaller.Core.Winget;

namespace MyUninstaller.App.ViewModels;

/// <summary>Satu paket winget.</summary>
public sealed class WingetPackageItemViewModel(WingetPackage package)
{
    public WingetPackage Package { get; } = package;

    public string Name => Package.Name;

    public string Id => Package.Id;

    public bool HasUpdate => Package.HasUpdate;

    public string VersionText => Package.HasUpdate
        ? Strings.Format("Winget_VersionUpdate", Package.Version, Package.Available)
        : Strings.Format("Winget_VersionCurrent", Package.Version);
}

/// <summary>Jendela "Pembaruan program" (versi 2.0): daftar paket winget, perbarui, perbarui semua, pasang ulang.</summary>
public sealed partial class WingetViewModel : ObservableObject, IDisposable
{
    private readonly IWingetClient _winget;
    private readonly Func<string, string, bool> _confirm;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _closing = new();

    /// <param name="confirm">Konfirmasi (judul tombol, pesan) → setuju?</param>
    public WingetViewModel(IWingetClient winget, Func<string, string, bool> confirm, ILogger logger)
    {
        _winget = winget;
        _confirm = confirm;
        _logger = logger;
        View = new ListCollectionView(Items) { Filter = item => !ShowOnlyUpdates || ((WingetPackageItemViewModel)item).HasUpdate };
    }

    public ObservableCollection<WingetPackageItemViewModel> Items { get; } = [];

    public ListCollectionView View { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpgradeCommand), nameof(ReinstallCommand))]
    public partial WingetPackageItemViewModel? Selected { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(UpgradeCommand), nameof(UpgradeAllCommand), nameof(ReinstallCommand))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool ShowOnlyUpdates { get; set; } = true;

    [ObservableProperty]
    public partial string SummaryText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? NoticeText { get; set; }

    [ObservableProperty]
    public partial string BusyText { get; set; } = string.Empty;

    public bool IsEmpty => !IsBusy && View.IsEmpty;

    /// <summary>True bila ada perintah winget yang dijalankan (daftar program utama perlu disegarkan).</summary>
    public bool AnyChanged { get; private set; }

    partial void OnShowOnlyUpdatesChanged(bool value)
    {
        View.Refresh();
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        BusyText = Strings.Get("Winget_Loading");
        NoticeText = null;
        string? keep = Selected?.Id;
        try
        {
            var result = await _winget.ListAsync(_closing.Token);
            Items.Clear();
            foreach (var package in result.Packages.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                Items.Add(new WingetPackageItemViewModel(package));
            }

            View.Refresh();
            Selected = Items.FirstOrDefault(i => i.Id == keep);
            int updates = Items.Count(i => i.HasUpdate);
            SummaryText = Strings.Format("Winget_Summary", IndonesianFormat.Count(Items.Count), IndonesianFormat.Count(updates));
            if (result.WingetMissing)
            {
                NoticeText = Strings.Get("Winget_Missing");
                SummaryText = string.Empty;
            }
            else if (result.Error is { } error)
            {
                NoticeText = Strings.Format("Winget_ListFailed", error);
            }
        }
        catch (OperationCanceledException)
        {
            // Jendela ditutup.
        }
        finally
        {
            IsBusy = false;
            UpgradeAllCommand.NotifyCanExecuteChanged();
        }
    }

    private bool IsIdle() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanUpgrade))]
    private Task UpgradeAsync() => RunAsync(WingetAction.Upgrade, Selected!.Package);

    private bool CanUpgrade() => !IsBusy && Selected is { HasUpdate: true };

    [RelayCommand(CanExecute = nameof(CanUpgradeAll))]
    private async Task UpgradeAllAsync()
    {
        int count = Items.Count(i => i.HasUpdate);
        if (_confirm(Strings.Get("Winget_UpgradeAll"), Strings.Format("Winget_ConfirmUpgradeAll", IndonesianFormat.Count(count))))
        {
            await RunAsync(WingetAction.UpgradeAll, null);
        }
    }

    private bool CanUpgradeAll() => !IsBusy && Items.Any(i => i.HasUpdate);

    [RelayCommand(CanExecute = nameof(CanReinstall))]
    private async Task ReinstallAsync()
    {
        var package = Selected!.Package;
        if (_confirm(Strings.Get("Winget_Reinstall"), Strings.Format("Winget_ConfirmReinstall", package.Name, package.Id)))
        {
            await RunAsync(WingetAction.Reinstall, package);
        }
    }

    private bool CanReinstall() => !IsBusy && Selected is not null;

    private async Task RunAsync(WingetAction action, WingetPackage? package)
    {
        IsBusy = true;
        BusyText = Strings.Get("Winget_ConsoleOpen");
        NoticeText = null;
        int? code;
        try
        {
            code = await _winget.RunInConsoleAsync(action, package?.Id, _closing.Token);
        }
        finally
        {
            IsBusy = false;
        }

        _logger.LogInformation("winget {Action} {Id}: kode {Code}", action, package?.Id, code);
        if (code is null)
        {
            NoticeText = Strings.Get("Winget_ConsoleFailed");
            return;
        }

        AnyChanged = true;
        await RefreshAsync();
        NoticeText = code == 0 ? Strings.Get("Winget_Done") : Strings.Format("Winget_DoneWithCode", $"0x{code:X8}");
    }

    public void Dispose()
    {
        _closing.Cancel();
    }
}

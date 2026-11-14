using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyUninstaller.App.Localization;
using MyUninstaller.App.Services;
using MyUninstaller.Core.Browsers;
using MyUninstaller.Core.Formatting;

namespace MyUninstaller.App.ViewModels;

/// <summary>Satu baris ekstensi.</summary>
public sealed class BrowserExtensionItemViewModel(BrowserExtension extension)
{
    public BrowserExtension Extension { get; } = extension;

    public string Name => Extension.Name;

    /// <summary>Judul kelompok: "Google Chrome · profil Wahyu".</summary>
    public string GroupText => Strings.Format("Extensions_Profile", BrowserExtensionService.BrowserName(Extension.Browser), Extension.ProfileName);

    public string DetailText => string.Join(" · ", new[]
    {
        Extension.Version,
        Extension.SizeBytes is { } size and > 0 ? IndonesianFormat.Size(size) : null,
        Extension.IsEnabled ? null : Strings.Get("Extensions_Disabled"),
    }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public string? Description => Extension.Description;

    public bool IsDisabled => !Extension.IsEnabled;

    public string Id => Extension.Id;
}

/// <summary>Jendela "Ekstensi browser" (PRD 1.3): daftar per browser dan profil, hapus, buka folder, salin ID.</summary>
public sealed partial class BrowserExtensionsViewModel : ObservableObject
{
    private readonly IBrowserExtensionReader _reader;
    private readonly BrowserExtensionService _service;
    private readonly IShellService _shell;
    private readonly Func<string, string, bool> _confirmFirefox;

    public BrowserExtensionsViewModel(
        IBrowserExtensionReader reader,
        BrowserExtensionService service,
        IShellService shell,
        Func<string, string, bool> confirmFirefox)
    {
        _reader = reader;
        _service = service;
        _shell = shell;
        _confirmFirefox = confirmFirefox;

        View = new ListCollectionView(Items);
        View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(BrowserExtensionItemViewModel.GroupText)));
        Load();
    }

    public ObservableCollection<BrowserExtensionItemViewModel> Items { get; } = [];

    public ListCollectionView View { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand), nameof(OpenFolderCommand), nameof(CopyIdCommand))]
    public partial BrowserExtensionItemViewModel? Selected { get; set; }

    [ObservableProperty]
    public partial string SummaryText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? NoticeText { get; set; }

    public bool IsEmpty => Items.Count == 0;

    /// <summary>True bila ada ekstensi Firefox yang dipindah ke karantina.</summary>
    public bool AnyRemoved { get; private set; }

    [RelayCommand]
    private void Refresh()
    {
        NoticeText = null;
        Load();
    }

    private void Load()
    {
        string? keep = Selected?.Extension.Key;
        Items.Clear();
        foreach (var extension in _reader.GetAll())
        {
            Items.Add(new BrowserExtensionItemViewModel(extension));
        }

        View.Refresh();
        Selected = Items.FirstOrDefault(i => i.Extension.Key == keep);
        int profiles = Items.Select(i => (i.Extension.Browser, i.Extension.ProfileFolder)).Distinct().Count();
        SummaryText = Strings.Format("Extensions_Summary", IndonesianFormat.Count(Items.Count), IndonesianFormat.Count(profiles));
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Remove()
    {
        var result = _service.Remove(Selected!.Extension, _confirmFirefox);
        if (result.Message.Length > 0)
        {
            NoticeText = result.Message;
        }

        if (result.Removed)
        {
            AnyRemoved = true;
            Load();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenFolder()
    {
        string path = Selected!.Extension.Path;
        _shell.OpenFolder(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyId()
    {
        _shell.CopyText(Selected!.Extension.Id);
        NoticeText = Strings.Get("Extensions_IdCopied");
    }

    private bool HasSelection() => Selected is not null;
}

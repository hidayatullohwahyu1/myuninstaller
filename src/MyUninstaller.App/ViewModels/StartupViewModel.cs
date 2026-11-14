using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyUninstaller.App.Localization;
using MyUninstaller.App.Services;
using MyUninstaller.Core.Formatting;
using MyUninstaller.Core.Hunter;
using MyUninstaller.Core.Models;
using MyUninstaller.Core.Startup;

namespace MyUninstaller.App.ViewModels;

/// <summary>Satu baris program awal.</summary>
public sealed partial class StartupItemViewModel : ObservableObject
{
    private readonly Func<StartupItemViewModel, bool, bool> _setEnabled;
    private bool _reverting;

    public StartupItemViewModel(StartupEntry entry, InstalledProgram? program, Func<StartupItemViewModel, bool, bool> setEnabled)
    {
        Entry = entry;
        Program = program;
        _setEnabled = setEnabled;
        IsEnabled = entry.IsEnabled;
    }

    public StartupEntry Entry { get; private set; }

    public InstalledProgram? Program { get; }

    public string Name => Entry.Name;

    public string Command => Entry.Command;

    public string SourceText => Strings.Get("StartupSource_" + Entry.Source);

    public string? ProgramText => Program is null ? null : Strings.Format("Startup_Program", Program.DisplayName);

    public bool CanOpenLocation => Entry.ExecutablePath is { } exe && File.Exists(exe);

    [ObservableProperty]
    public partial ImageSource? Icon { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    public partial bool IsEnabled { get; set; }

    public string StateText => Strings.Get(IsEnabled ? "Startup_Enabled" : "Startup_Disabled");

    /// <summary>Kotak centang diubah pengguna: terapkan, dan kembalikan tampilannya bila gagal.</summary>
    partial void OnIsEnabledChanged(bool value)
    {
        if (_reverting || value == Entry.IsEnabled)
        {
            return;
        }

        if (_setEnabled(this, value))
        {
            Entry = Entry with { IsEnabled = value };
        }
        else
        {
            _reverting = true;
            IsEnabled = !value;
            _reverting = false;
        }
    }
}

/// <summary>Jendela "Program awal" (PRD 1.2): lihat, aktifkan/nonaktifkan, buka lokasi berkas.</summary>
public sealed partial class StartupViewModel : ObservableObject
{
    private readonly IStartupRepository _repository;
    private readonly IShellService _shell;
    private readonly Func<IReadOnlyList<InstalledProgram>> _programs;
    private readonly IconLoader? _icons;
    private readonly ILogger _logger;

    public StartupViewModel(
        IStartupRepository repository,
        IShellService shell,
        Func<IReadOnlyList<InstalledProgram>> programs,
        IconLoader? icons,
        ILogger logger)
    {
        _repository = repository;
        _shell = shell;
        _programs = programs;
        _icons = icons;
        _logger = logger;
        Load();
    }

    public ObservableCollection<StartupItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenLocationCommand))]
    public partial StartupItemViewModel? Selected { get; set; }

    [ObservableProperty]
    public partial string SummaryText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? NoticeText { get; set; }

    public bool IsEmpty => Items.Count == 0;

    [RelayCommand]
    private void Refresh()
    {
        NoticeText = null;
        Load();
    }

    private void Load()
    {
        Items.Clear();
        var programs = _programs();
        foreach (var entry in _repository.GetAll())
        {
            var program = ProgramMatcher.FindByExecutable(entry.ExecutablePath, programs);
            var item = new StartupItemViewModel(entry, program, Apply);
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(StartupItemViewModel.IsEnabled))
                {
                    UpdateSummary();
                }
            };

            if (_icons is not null && entry.ExecutablePath is { } exe)
            {
                item.Icon = _icons.Load(new InstalledProgram
                {
                    Id = entry.Id,
                    DisplayName = entry.Name,
                    Source = ProgramSource.Registry64,
                    IconPath = exe,
                });
            }

            Items.Add(item);
        }

        OnPropertyChanged(nameof(IsEmpty));
        UpdateSummary();
    }

    private bool Apply(StartupItemViewModel item, bool enabled)
    {
        bool ok = _repository.SetEnabled(item.Entry, enabled);
        NoticeText = ok
            ? Strings.Format("Startup_Changed", item.Name, Strings.Get(enabled ? "Startup_NowEnabled" : "Startup_NowDisabled"))
            : Strings.Format("Startup_Failed", item.Name);
        if (!ok)
        {
            _logger.LogWarning("Program awal {Name} gagal diubah", item.Name);
        }

        return ok;
    }

    private void UpdateSummary() =>
        SummaryText = Strings.Format("Startup_Summary", IndonesianFormat.Count(Items.Count), IndonesianFormat.Count(Items.Count(i => i.IsEnabled)));

    [RelayCommand(CanExecute = nameof(CanOpenLocation))]
    private void OpenLocation()
    {
        if (Selected?.Entry.ExecutablePath is { } exe && Path.GetDirectoryName(exe) is { } folder)
        {
            _shell.OpenFolder(folder);
        }
    }

    private bool CanOpenLocation() => Selected is { CanOpenLocation: true };
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyUninstaller.App.Localization;
using MyUninstaller.Core.Formatting;
using MyUninstaller.Core.Leftovers;
using MyUninstaller.Core.Safety;

namespace MyUninstaller.App.ViewModels;

/// <summary>Satu baris di layar hasil sisa.</summary>
public sealed partial class LeftoverItemViewModel : ObservableObject
{
    public LeftoverItemViewModel(LeftoverItem item)
    {
        Item = item;
        IsChecked = ConfidenceScorer.IsCheckedByDefault(item.Score);
    }

    public LeftoverItem Item { get; }

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    /// <summary>Hasil setelah dihapus; null sebelum diproses.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult), nameof(IsEditable))]
    public partial string? ResultText { get; set; }

    [ObservableProperty]
    public partial bool ResultIsProblem { get; set; }

    public bool HasResult => ResultText is not null;

    public bool IsEditable => ResultText is null;

    public string ProgramName => Item.ProgramName;

    public LeftoverGroup Group => Item.Group;

    /// <summary>Label kelompok untuk pengelompokan tampilan (urutan: Aman, Mungkin, Ragu).</summary>
    public string GroupLabel => Strings.Get(Item.Group switch
    {
        LeftoverGroup.Safe => "LeftoverGroup_Safe",
        LeftoverGroup.Maybe => "LeftoverGroup_Maybe",
        _ => "LeftoverGroup_Doubtful",
    });

    public int GroupOrder => (int)Item.Group;

    public string KindText => Strings.Get("Kind_" + Item.Kind);

    public string KindGlyph => Item.Kind switch
    {
        LeftoverKind.Folder => "",
        LeftoverKind.File => "",
        LeftoverKind.Shortcut => "",
        LeftoverKind.Service => "",
        LeftoverKind.ScheduledTask => "",
        _ => "",
    };

    public string LocationText => Item.ValueName is null ? Item.Location : $"{Item.Location}  →  {Item.ValueName}";

    public string EvidenceText => Strings.Get("Evidence_" + Item.Evidence);

    public string? DetailText => Item.Detail;

    public string ToolTipText => Item.Detail is null ? $"{KindText} · {EvidenceText}" : $"{KindText} · {EvidenceText}\n{Item.Detail}";

    public string SizeText => Item.SizeBytes is { } size && size > 0 ? IndonesianFormat.Size(size) : string.Empty;

    public int Score => Item.Score;
}

/// <summary>
/// Layar hasil sisa (PRD langkah 7–8): temuan dikelompokkan per program lalu Aman/Mungkin/Ragu, bisa dicentang,
/// "Hapus yang dicentang" lewat SafeRemover, lalu ringkasan dan Batalkan.
/// </summary>
public sealed partial class LeftoverResultsViewModel : ObservableObject
{
    private readonly string _sessionId;
    private readonly SafeRemover _remover;
    private readonly SessionRestorer _restorer;
    private readonly ISessionJournal _journal;
    private readonly Func<int, bool> _confirmRisky;
    private readonly Func<bool> _confirmUndo;
    private readonly ILogger _logger;

    public LeftoverResultsViewModel(
        IReadOnlyList<LeftoverItem> items,
        string sessionId,
        SafeRemover remover,
        SessionRestorer restorer,
        ISessionJournal journal,
        Func<int, bool> confirmRisky,
        Func<bool> confirmUndo,
        ILogger logger)
    {
        _sessionId = sessionId;
        _remover = remover;
        _restorer = restorer;
        _journal = journal;
        _confirmRisky = confirmRisky;
        _confirmUndo = confirmUndo;
        _logger = logger;

        Items = [.. items.Select(i => new LeftoverItemViewModel(i))];
        foreach (var item in Items)
        {
            item.PropertyChanged += OnItemChanged;
        }

        View = new ListCollectionView(Items) { Filter = o => ShowDoubtful || ((LeftoverItemViewModel)o).Group != LeftoverGroup.Doubtful };
        View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(LeftoverItemViewModel.ProgramName)));
        View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(LeftoverItemViewModel.GroupLabel)));
        View.SortDescriptions.Add(new SortDescription(nameof(LeftoverItemViewModel.ProgramName), ListSortDirection.Ascending));
        View.SortDescriptions.Add(new SortDescription(nameof(LeftoverItemViewModel.GroupOrder), ListSortDirection.Ascending));
        View.SortDescriptions.Add(new SortDescription(nameof(LeftoverItemViewModel.Score), ListSortDirection.Descending));

        HeaderText = Strings.Format("Leftovers_Header", IndonesianFormat.Count(Items.Count));
        DoubtfulCount = Items.Count(i => i.Group == LeftoverGroup.Doubtful);
        UpdateChecked();
    }

    public ObservableCollection<LeftoverItemViewModel> Items { get; }

    public ListCollectionView View { get; }

    public string HeaderText { get; }

    public int DoubtfulCount { get; }

    public string ShowDoubtfulText => Strings.Format("Leftovers_ShowDoubtful", DoubtfulCount);

    public bool HasDoubtful => DoubtfulCount > 0;

    [ObservableProperty]
    public partial bool ShowDoubtful { get; set; }

    [ObservableProperty]
    public partial string CheckedText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCheckedCommand))]
    public partial int CheckedCount { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCheckedCommand), nameof(UndoCommand), nameof(SkipCommand))]
    public partial bool IsWorking { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCheckedCommand), nameof(UndoCommand))]
    public partial bool IsDone { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    public partial bool IsUndone { get; set; }

    [ObservableProperty]
    public partial string? SummaryText { get; set; }

    [ObservableProperty]
    public partial string? SummaryDetailText { get; set; }

    /// <summary>Jumlah item yang benar-benar dipindahkan (untuk menyegarkan daftar program).</summary>
    public int RemovedCount { get; private set; }

    public event EventHandler? RequestClose;

    partial void OnShowDoubtfulChanged(bool value) => View.Refresh();

    [RelayCommand]
    private void SelectSafe()
    {
        foreach (var item in Items.Where(i => i.IsEditable))
        {
            item.IsChecked = item.Group == LeftoverGroup.Safe;
        }
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var item in Items.Where(i => i.IsEditable))
        {
            item.IsChecked = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveCheckedAsync()
    {
        var checkedItems = Items.Where(i => i.IsChecked && i.IsEditable).ToList();

        // PRD: konfirmasi kedua untuk item yang tidak berlabel Aman (skor rendah / di luar folder program).
        int risky = checkedItems.Count(i => i.Group != LeftoverGroup.Safe);
        if (risky > 0 && !_confirmRisky(risky))
        {
            return;
        }

        IsWorking = true;
        try
        {
            var byTarget = checkedItems.ToDictionary(i => i.Item.ToRemovalTarget());
            var results = await Task.Run(() => _remover.Remove(_sessionId, byTarget.Keys));

            foreach (var result in results)
            {
                var row = byTarget[result.Target];
                row.ResultIsProblem = result.Status is RemovalStatus.Failed or RemovalStatus.Protected;
                row.ResultText = Strings.Get("Result_" + result.Status) + (result.Detail is null ? string.Empty : $" ({result.Detail})");
            }

            Summarize(results);
            _journal.FinishSession(_sessionId, JournalSessionStatus.Completed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gagal membersihkan sisa");
            SummaryText = Strings.Format("Outcome_Error", ex.Message);
        }
        finally
        {
            IsWorking = false;
            IsDone = true;
        }
    }

    private bool CanRemove() => !IsWorking && !IsDone && CheckedCount > 0;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        if (!_confirmUndo())
        {
            return;
        }

        IsWorking = true;
        try
        {
            var summary = await Task.Run(() => _restorer.Restore(_sessionId));
            SummaryText = Strings.Format("Leftovers_Undone", summary.FilesRestored, summary.KeysRestored);
            SummaryDetailText = summary.Failed > 0 ? Strings.Format("Recovery_DoneWithFailures", summary.FilesRestored, summary.KeysRestored, summary.Failed) : null;
            IsUndone = true;
        }
        finally
        {
            IsWorking = false;
        }
    }

    private bool CanUndo() => IsDone && !IsUndone && !IsWorking && RemovedCount > 0;

    [RelayCommand(CanExecute = nameof(CanSkip))]
    private void Skip() => RequestClose?.Invoke(this, EventArgs.Empty);

    private bool CanSkip() => !IsWorking;

    private void Summarize(IReadOnlyList<RemovalResult> results)
    {
        var done = results.Where(r => r.Status is RemovalStatus.Removed or RemovalStatus.PendingReboot).ToList();
        RemovedCount = done.Count;
        int paths = done.Count(r => r.Target.Kind == RemovalTargetKind.Path);
        int registry = done.Count(r => r.Target.Kind is RemovalTargetKind.RegistryKey or RemovalTargetKind.RegistryValue);
        int other = done.Count - paths - registry;
        long bytes = done.Sum(r => r.Bytes);
        int problems = results.Count(r => r.Status is RemovalStatus.Failed or RemovalStatus.Protected);

        SummaryText = Strings.Format("Leftovers_Done", IndonesianFormat.Count(done.Count));
        SummaryDetailText = Strings.Format("Leftovers_DoneDetail", paths, registry, other, IndonesianFormat.Size(bytes))
            + (problems > 0 ? Environment.NewLine + Strings.Format("Leftovers_DoneFailures", problems) : string.Empty);
        _logger.LogInformation("Sisa dibersihkan: {Paths} berkas/folder, {Registry} registri, {Other} layanan/tugas, {Bytes} bita, {Problems} masalah",
            paths, registry, other, bytes, problems);
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LeftoverItemViewModel.IsChecked))
        {
            UpdateChecked();
        }
    }

    private void UpdateChecked()
    {
        var checkedItems = Items.Where(i => i.IsChecked).ToList();
        CheckedCount = checkedItems.Count;
        CheckedText = Strings.Format("Leftovers_Checked", IndonesianFormat.Count(CheckedCount),
            IndonesianFormat.Size(checkedItems.Sum(i => i.Item.SizeBytes ?? 0)));
    }
}

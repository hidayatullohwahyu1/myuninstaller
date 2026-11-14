using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace MyUninstaller.App.ViewModels;

/// <summary>
/// ObservableCollection yang bisa diganti seluruh isinya dengan satu notifikasi Reset,
/// supaya ratusan program tidak memicu ratusan pembaruan tampilan.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (T item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

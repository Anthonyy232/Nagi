using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Nagi.Presentation.Helpers;

/// <summary>
/// Batches collection notifications for bulk updates.
/// </summary>
public class ObservableRangeCollection<T> : ObservableCollection<T>
{
    public ObservableRangeCollection() { }
    public ObservableRangeCollection(IEnumerable<T> collection) : base(collection) { }

    public void AddRange(IEnumerable<T> collection)
    {
        CheckReentrancy();

        var addedItems = collection.ToList();
        if (addedItems.Count == 0) return;

        var startingIndex = Count;
        foreach (var item in addedItems)
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, addedItems, startingIndex));
    }

    public void ReplaceRange(IEnumerable<T> collection)
    {
        CheckReentrancy();

        // Snapshot before clearing: the input may be a query over this collection.
        var newItems = collection.ToList();
        Items.Clear();
        foreach (var item in newItems)
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void AppendOrReplace(IEnumerable<T> collection, bool append)
    {
        if (append) AddRange(collection);
        else ReplaceRange(collection);
    }
}

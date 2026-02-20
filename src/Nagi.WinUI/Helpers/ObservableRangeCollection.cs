using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Nagi.WinUI.Helpers;

/// <summary>
/// Represents a dynamic data collection that provides notifications when items get added, removed, or when the whole list is refreshed.
/// Optimizes bulk operations by suppressing notifications until the operation completes.
/// </summary>
/// <typeparam name="T">The type of elements in the collection.</typeparam>
public class ObservableRangeCollection<T> : ObservableCollection<T>
{
    public ObservableRangeCollection() : base() { }
    public ObservableRangeCollection(IEnumerable<T> collection) : base(collection) { }
    public ObservableRangeCollection(List<T> list) : base(list) { }

    /// <summary>
    /// Adds the elements of the specified collection to the end of the ObservableCollection.
    /// </summary>
    public void AddRange(IEnumerable<T> collection)
    {
        if (collection == null) return;

        CheckReentrancy();

        var startingIndex = Count;
        var addedItems = new List<T>();
        foreach (var item in collection)
        {
            Items.Add(item);
            addedItems.Add(item);
        }

        if (addedItems.Count == 0) return;

        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, addedItems, startingIndex));
    }

    /// <summary>
    /// Removes the elements of the specified collection from the ObservableCollection.
    /// </summary>
    public void RemoveRange(IEnumerable<T> collection)
    {
        if (collection == null) return;

        CheckReentrancy();

        var removed = false;
        foreach (var item in collection)
        {
            if (Items.Remove(item))
            {
                removed = true;
            }
        }

        if (!removed) return;

        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>
    /// Clears the current collection and replaces it with the specified collection.
    /// </summary>
    public void ReplaceRange(IEnumerable<T> collection)
    {
        if (collection == null) return;

        CheckReentrancy();

        Items.Clear();
        foreach (var item in collection)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

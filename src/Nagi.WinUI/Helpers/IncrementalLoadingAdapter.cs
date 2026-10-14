using System;
using System.Collections;
using System.Collections.Specialized;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Data;
using Nagi.Presentation.Models;
using Windows.Foundation;

namespace Nagi.WinUI.Helpers;

/// <summary>
///     Presents an <see cref="IIncrementalItemSource" /> collection to a WinUI list as
///     <see cref="ISupportIncrementalLoading" />. List edits such as drag reordering go straight to the source.
/// </summary>
public sealed partial class IncrementalLoadingAdapter : IList, INotifyCollectionChanged, ISupportIncrementalLoading
{
    private readonly IList _items;
    private readonly IIncrementalItemSource _source;

    /// <param name="source">The collection, which must also implement <see cref="IList" /> and <see cref="INotifyCollectionChanged" />.</param>
    public IncrementalLoadingAdapter(IIncrementalItemSource source)
    {
        _items = (IList)source;
        _source = source;
        ((INotifyCollectionChanged)source).CollectionChanged += (_, e) => CollectionChanged?.Invoke(this, e);
    }

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public bool HasMoreItems => _source.HasMoreItems;

    public IAsyncOperation<LoadMoreItemsResult> LoadMoreItemsAsync(uint count) => LoadAsync().AsAsyncOperation();

    private async Task<LoadMoreItemsResult> LoadAsync() =>
        new() { Count = (uint)await _source.LoadMoreItemsAsync() };

    public object? this[int index] { get => _items[index]; set => _items[index] = value; }
    public bool IsFixedSize => _items.IsFixedSize;
    public bool IsReadOnly => _items.IsReadOnly;
    public int Count => _items.Count;
    public bool IsSynchronized => _items.IsSynchronized;
    public object SyncRoot => _items.SyncRoot;
    public int Add(object? value) => _items.Add(value);
    public void Clear() => _items.Clear();
    public bool Contains(object? value) => _items.Contains(value);
    public int IndexOf(object? value) => _items.IndexOf(value);
    public void Insert(int index, object? value) => _items.Insert(index, value);
    public void Remove(object? value) => _items.Remove(value);
    public void RemoveAt(int index) => _items.RemoveAt(index);
    public void CopyTo(Array array, int index) => _items.CopyTo(array, index);
    public IEnumerator GetEnumerator() => _items.GetEnumerator();
}

using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading.Tasks;
using Nagi.Core.Platform;
using Nagi.Presentation.Common;

namespace Nagi.Presentation.Settings;

/// <summary>
///     A settings list the user reorders by dragging and switches items on and off in. Every change saves the whole
///     list, debounced so that a drag, which removes and re-inserts an item, saves once.
/// </summary>
public sealed class ReorderableToggleList<T> : ObservableRangeCollection<T>, IDisposable where T : class, IToggleItem
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(300);

    private readonly Action<IReadOnlyList<T>> _save;
    private readonly IDispatcherService _dispatcherService;
    private readonly Func<T, bool>? _isPinnedFirst;
    private readonly Debouncer _saveDebouncer;
    private List<T> _subscribedItems = [];
    private bool _isLoading;

    /// <param name="save">Saves the list in its current order. Runs on the UI thread.</param>
    /// <param name="isPinnedFirst">Marks an item that must stay at the top, whatever the user drags.</param>
    public ReorderableToggleList(Action<IReadOnlyList<T>> save, IDispatcherService dispatcherService,
        TimeProvider? timeProvider = null, Func<T, bool>? isPinnedFirst = null)
    {
        _save = save;
        _dispatcherService = dispatcherService;
        _isPinnedFirst = isPinnedFirst;
        _saveDebouncer = new Debouncer(SaveDelay, timeProvider);
    }

    internal Task PendingSave => _saveDebouncer.PendingInvocation;

    /// <summary>
    ///     Shows <paramref name="items" /> without saving them.
    /// </summary>
    public void Load(IEnumerable<T> items)
    {
        _isLoading = true;
        try
        {
            ReplaceRange(_isPinnedFirst is null ? items : items.OrderByDescending(_isPinnedFirst));
        }
        finally
        {
            _isLoading = false;
        }
    }

    /// <summary>
    ///     Replaces the list with <paramref name="items" /> and saves it at once, dropping any save still waiting.
    /// </summary>
    public void Reset(IEnumerable<T> items)
    {
        _saveDebouncer.Cancel();
        Load(items);
        _save(this.ToList());
    }

    public void Dispose()
    {
        _saveDebouncer.Dispose();
        foreach (var item in _subscribedItems) item.PropertyChanged -= OnItemPropertyChanged;
        _subscribedItems = [];
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        // A reset doesn't name the items it replaced, so every change re-subscribes the whole list.
        foreach (var item in _subscribedItems) item.PropertyChanged -= OnItemPropertyChanged;
        _subscribedItems = this.ToList();
        foreach (var item in _subscribedItems) item.PropertyChanged += OnItemPropertyChanged;

        base.OnCollectionChanged(e);
        if (_isLoading) return;

        if (_isPinnedFirst is null)
        {
            QueueSave();
            return;
        }

        // Deferred: the list can't change while it is still raising this event, and a drag is still in progress.
        _dispatcherService.TryEnqueue(() =>
        {
            var pinnedIndex = this.ToList().FindIndex(item => _isPinnedFirst(item));
            if (pinnedIndex > 0)
            {
                _isLoading = true;
                try { Move(pinnedIndex, 0); }
                finally { _isLoading = false; }
            }

            QueueSave();
        });
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_isLoading && e.PropertyName == nameof(IToggleItem.IsEnabled)) QueueSave();
    }

    private void QueueSave()
    {
        var snapshot = this.ToList();
        _saveDebouncer.Trigger(token => _dispatcherService.EnqueueAsync(() =>
        {
            if (!token.IsCancellationRequested) _save(snapshot);
            return Task.CompletedTask;
        }));
    }
}

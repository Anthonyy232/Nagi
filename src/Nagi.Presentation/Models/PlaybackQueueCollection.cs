using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Resources;

namespace Nagi.Presentation.Models;

public sealed class PlaybackQueueCollection(IMusicPlaybackService playbackService, ILogger logger)
    : ObservableCollection<QueueEntry>, IIncrementalItemSource, IDisposable
{
    private Guid[] _ids = [];
    private int _loadedCount;
    private CancellationTokenSource _refreshCts = new();
    private CancellationTokenSource _durationCts = new();
    private Task<int>? _loadTask;
    private Task? _durationTask;
    private bool _isActive;
    private bool _showTotalDuration;
    private bool _isDisposed;
    private bool _reloadMetadata;
    private bool _isLoading;
    private bool _hasLoadError;

    public int Version { get; private set; }
    public bool HasMoreItems => !_isDisposed && _isActive && !HasLoadError && _loadedCount < _ids.Length;
    public bool IsLoading => _isLoading;
    public bool ShowInitialLoading => IsLoading && Count == 0;
    public bool ShowMoreLoading => IsLoading && Count > 0;
    public bool HasLoadError => _hasLoadError;
    public bool ShowEmptyState => _isActive && !IsLoading && !HasLoadError && Count == 0;
    public string TotalDurationText { get; private set; } = string.Empty;
    public string TrackCountText => string.Format(Strings.QueuePanel_TrackCount, _ids.Length);
    private int FirstMovableIndex => playbackService.CurrentTrack?.Id == _ids.FirstOrDefault() ? 1 : 0;

    public void SetActive(bool active)
    {
        if (_isDisposed) return;
        _isActive = active;
        if (active) Refresh();
        else
        {
            _refreshCts.Cancel();
            _durationCts.Cancel();
            SetLoadState(false, false);
        }
    }

    public void SetShowTotalDuration(bool show)
    {
        if (_isDisposed) return;
        _showTotalDuration = show;
        if (show && _isActive && TotalDurationText.Length == 0 && _durationTask?.IsCompleted != false)
            _durationTask = UpdateDurationAsync(_durationCts.Token);
    }

    public void Refresh(bool reloadMetadata = false)
    {
        if (_isDisposed) return;
        _reloadMetadata |= reloadMetadata;
        if (!_isActive) return;
        var pageSize = Math.Max(128, Math.Max(_loadedCount, Count));
        var cachedEntries = !_reloadMetadata
            ? this.ToDictionary(entry => entry.Id) : new Dictionary<Guid, QueueEntry>();
        Version++;
        _refreshCts.Cancel();
        _refreshCts.Dispose();
        _refreshCts = new CancellationTokenSource();
        var queue = playbackService.IsShuffleEnabled ? playbackService.ShuffledQueue : playbackService.PlaybackQueue;
        var currentIndex = playbackService.IsShuffleEnabled ? playbackService.CurrentShuffledIndex : playbackService.CurrentQueueIndex;
        var ids = queue.Skip(Math.Max(0, currentIndex)).ToArray();
        var durationChanged = _reloadMetadata || _ids.Length != ids.Length || !_ids.ToHashSet().SetEquals(ids)
            || (_durationCts.IsCancellationRequested && TotalDurationText.Length == 0);
        _ids = ids;
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(TrackCountText)));
        _loadedCount = 0;
        _loadTask = null;
        SetLoadState(false, false);
        if (_ids.Length == 0) Clear();
        if (durationChanged)
        {
            _durationCts.Cancel();
            _durationCts.Dispose();
            _durationCts = new CancellationTokenSource();
            _durationTask = null;
            TotalDurationText = string.Empty;
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(TotalDurationText)));
        }
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(HasMoreItems)));
        _loadTask = LoadPageAsync(_refreshCts.Token, pageSize, cachedEntries);
        if (_showTotalDuration && durationChanged) _durationTask = UpdateDurationAsync(_durationCts.Token);
    }

    public Task<int> LoadMoreItemsAsync()
    {
        if (_isDisposed || !_isActive || HasLoadError) return Task.FromResult(0);
        if (_loadTask is null || _loadTask.IsCompleted)
            _loadTask = LoadPageAsync(_refreshCts.Token);
        return _loadTask;
    }

    private async Task<int> LoadPageAsync(CancellationToken token, int pageSize = 128,
        Dictionary<Guid, QueueEntry>? cachedEntries = null)
    {
        var added = 0;
        var refreshedEntries = cachedEntries is null ? null : new List<QueueEntry>();
        SetLoadState(true, false);
        try
        {
            while (HasMoreItems && added == 0)
            {
                var offset = _loadedCount;
                var ids = _ids.Skip(offset).Take(pageSize).ToArray();
                var missingIds = cachedEntries is null ? ids : ids.Where(id => !cachedEntries.ContainsKey(id)).ToArray();
                var songs = missingIds.Length > 0 ? await playbackService.GetQueueSongsAsync(missingIds, token) : null;
                token.ThrowIfCancellationRequested();
                _loadedCount += ids.Length;
                for (var i = 0; i < ids.Length; i++)
                {
                    var entry = cachedEntries?.GetValueOrDefault(ids[i]);
                    if (entry is null)
                    {
                        if (songs is null || !songs.TryGetValue(ids[i], out var song)) continue;
                        entry = new QueueEntry(song, offset + i + 1);
                    }
                    entry.Position = offset + i + 1;
                    if (refreshedEntries is not null) refreshedEntries.Add(entry);
                    else Add(entry);
                    added++;
                }
            }
            if (refreshedEntries is not null)
            {
                var retainedEntries = refreshedEntries.ToHashSet();
                for (var index = Count - 1; index >= 0; index--)
                    if (!retainedEntries.Contains(this[index])) RemoveAt(index);
                for (var index = 0; index < refreshedEntries.Count; index++)
                {
                    var entry = refreshedEntries[index];
                    var oldIndex = IndexOf(entry);
                    if (oldIndex < 0) Insert(index, entry);
                    else if (oldIndex != index) Move(oldIndex, index);
                }
                _reloadMetadata = false;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested && !_isDisposed)
            {
                if (cachedEntries is not null) Clear();
                SetLoadState(false, true);
            }
            logger.LogError(ex, "Failed to load the playback queue.");
        }
        finally
        {
            if (!token.IsCancellationRequested && !_isDisposed) SetLoadState(false, HasLoadError);
        }
        return added;
    }

    private void SetLoadState(bool isLoading, bool hasLoadError)
    {
        _isLoading = isLoading;
        _hasLoadError = hasLoadError;
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(IsLoading)));
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(HasLoadError)));
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(ShowEmptyState)));
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(ShowInitialLoading)));
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(ShowMoreLoading)));
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(HasMoreItems)));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        base.OnCollectionChanged(e);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(ShowEmptyState)));
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(ShowInitialLoading)));
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(ShowMoreLoading)));
    }

    private async Task UpdateDurationAsync(CancellationToken token)
    {
        try
        {
            var duration = await playbackService.GetQueueDurationAsync(_ids, token);
            token.ThrowIfCancellationRequested();
            TotalDurationText = $"{(long)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}";
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(TotalDurationText)));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogError(ex, "Failed to calculate queue duration."); }
    }

    /// <summary>The playing track stays where it is.</summary>
    public bool CanMove(QueueEntry entry) => entry.Id != playbackService.CurrentTrack?.Id;

    public void CompleteReorder(QueueEntry entry, int version)
    {
        if (version != Version) { Refresh(); return; }
        var index = IndexOf(entry);
        if (index < FirstMovableIndex || !CanMove(entry)) { Refresh(); return; }
        Guid? beforeId = index + 1 < Count ? this[index + 1].Id
            : _loadedCount < _ids.Length ? _ids[_loadedCount] : null;
        playbackService.MoveQueueItem(entry.Id, beforeId);
    }

    public void Move(QueueEntry entry, int direction)
    {
        var index = Array.IndexOf(_ids, entry.Id);
        if (index < FirstMovableIndex || index + direction < FirstMovableIndex || index + direction >= _ids.Length) return;
        var beforeIndex = direction < 0 ? index - 1 : index + 2;
        playbackService.MoveQueueItem(entry.Id, beforeIndex < _ids.Length ? _ids[beforeIndex] : null);
    }

    public async Task RemoveAsync(QueueEntry entry)
    {
        try { await playbackService.RemoveFromQueueAsync(entry.Id); }
        catch (Exception ex) { logger.LogError(ex, "Failed to remove queue entry."); }
    }

    public async Task PlayAsync(QueueEntry entry)
    {
        var queue = playbackService.PlaybackQueue;
        for (var i = 0; i < queue.Count; i++)
        {
            if (queue[i] != entry.Id) continue;
            try { await playbackService.PlayQueueItemAsync(i); }
            catch (Exception ex) { logger.LogError(ex, "Failed to play queue entry."); }
            return;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _isActive = false;
        _refreshCts.Cancel();
        _refreshCts.Dispose();
        _durationCts.Cancel();
        _durationCts.Dispose();
        SetLoadState(false, false);
    }
}

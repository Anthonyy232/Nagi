using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Data;
using Nagi.Core.Services.Abstractions;
using Windows.Foundation;

namespace Nagi.WinUI.Models;

public sealed class PlaybackQueueCollection(IMusicPlaybackService playbackService, ILogger logger)
    : ObservableCollection<QueueEntry>, ISupportIncrementalLoading, IDisposable
{
    private Guid[] _ids = [];
    private int _loadedCount;
    private CancellationTokenSource _refreshCts = new();
    private CancellationTokenSource _durationCts = new();
    private Task<LoadMoreItemsResult>? _loadTask;
    private Task? _durationTask;
    private bool _isActive;
    private bool _showTotalDuration;
    private bool _isDisposed;
    private bool _reloadMetadata;

    public int Version { get; private set; }
    public bool HasMoreItems => _isActive && _loadedCount < _ids.Length;
    public string TotalDurationText { get; private set; } = string.Empty;

    public void SetActive(bool active)
    {
        if (_isDisposed) return;
        _isActive = active;
        if (active) Refresh(reloadMetadata: true);
        else
        {
            _refreshCts.Cancel();
            _durationCts.Cancel();
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
        var pageSize = Math.Max(128, Math.Max(_loadedCount, Count));
        var cachedEntries = _isActive && !_reloadMetadata
            ? this.ToDictionary(entry => entry.Id) : new Dictionary<Guid, QueueEntry>();
        Version++;
        _refreshCts.Cancel();
        _refreshCts.Dispose();
        _refreshCts = new CancellationTokenSource();
        var ids = _isActive
            ? (playbackService.IsShuffleEnabled ? playbackService.ShuffledQueue : playbackService.PlaybackQueue).ToArray()
            : [];
        var durationChanged = _reloadMetadata || _ids.Length != ids.Length || !_ids.ToHashSet().SetEquals(ids);
        _ids = ids;
        _loadedCount = 0;
        _loadTask = null;
        if (!_isActive || _ids.Length == 0) Clear();
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
        if (!_isActive) return;
        _loadTask = LoadPageAsync(_refreshCts.Token, pageSize, cachedEntries);
        if (_showTotalDuration && durationChanged) _durationTask = UpdateDurationAsync(_durationCts.Token);
    }

    public IAsyncOperation<LoadMoreItemsResult> LoadMoreItemsAsync(uint count)
    {
        if (_isDisposed || !_isActive)
            return Task.FromResult(new LoadMoreItemsResult()).AsAsyncOperation();
        if (_loadTask is null || _loadTask.IsCompleted)
            _loadTask = LoadPageAsync(_refreshCts.Token);
        return _loadTask.AsAsyncOperation();
    }

    private async Task<LoadMoreItemsResult> LoadPageAsync(CancellationToken token, int pageSize = 128,
        Dictionary<Guid, QueueEntry>? cachedEntries = null)
    {
        uint added = 0;
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
                    if (cachedEntries is not null && added < Count)
                    {
                        if (!ReferenceEquals(this[(int)added], entry)) this[(int)added] = entry;
                    }
                    else Add(entry);
                    added++;
                }
            }
            if (cachedEntries is not null)
            {
                while (Count > added) RemoveAt(Count - 1);
                _reloadMetadata = false;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (cachedEntries is not null && !token.IsCancellationRequested) Clear();
            logger.LogError(ex, "Failed to load the playback queue.");
        }
        return new LoadMoreItemsResult { Count = added };
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

    public void CompleteReorder(QueueEntry entry, int version)
    {
        if (version != Version) { Refresh(); return; }
        var index = IndexOf(entry);
        if (index < 0) return;
        Guid? beforeId = index + 1 < Count ? this[index + 1].Id
            : _loadedCount < _ids.Length ? _ids[_loadedCount] : null;
        playbackService.MoveQueueItem(entry.Id, beforeId);
    }

    public void Move(QueueEntry entry, int direction)
    {
        var index = Array.IndexOf(_ids, entry.Id);
        if (index < 0 || index + direction < 0 || index + direction >= _ids.Length) return;
        var beforeIndex = direction < 0 ? index - 1 : index + 2;
        playbackService.MoveQueueItem(entry.Id, beforeIndex < _ids.Length ? _ids[beforeIndex] : null);
    }

    public Task RemoveAsync(QueueEntry entry) => playbackService.RemoveFromQueueAsync(entry.Id);

    public Task PlayAsync(QueueEntry entry)
    {
        var queue = playbackService.PlaybackQueue;
        for (var i = 0; i < queue.Count; i++)
            if (queue[i] == entry.Id) return playbackService.PlayQueueItemAsync(i);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _refreshCts.Cancel();
        _refreshCts.Dispose();
        _durationCts.Cancel();
        _durationCts.Dispose();
    }
}

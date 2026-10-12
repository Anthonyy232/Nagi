using System.Collections.ObjectModel;
using System.Runtime.InteropServices;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     The play order of a queue and, while shuffling, its shuffled order, with reverse indexes
///     for O(1) position lookups in very large queues.
/// </summary>
/// <remarks>
///     Indexes are rebuilt only when <see cref="RebuildIndex" />, <see cref="RebuildShuffledIndex" />
///     or <see cref="SyncShuffledWithOrder" /> is called, so a caller can batch several edits before
///     paying for a rebuild. Not thread-safe: <see cref="MusicPlaybackService" /> serialises access.
/// </remarks>
internal sealed class PlaybackQueue
{
    private List<Guid> _order = new();
    private List<Guid> _shuffled = new();
    private readonly Dictionary<Guid, int> _orderIndex = new();
    private readonly Dictionary<Guid, int> _shuffledIndex = new();

    public IReadOnlyList<Guid> Order => _order;
    public IReadOnlyList<Guid> Shuffled => _shuffled;

    /// <summary>
    ///     A read-only view of the current play order list. Replacing the order (for example with
    ///     <see cref="SetOrder" />) starts a new list, so earlier views keep showing the old one.
    /// </summary>
    public ReadOnlyCollection<Guid> OrderView() => _order.AsReadOnly();

    /// <inheritdoc cref="OrderView" />
    public ReadOnlyCollection<Guid> ShuffledView() => _shuffled.AsReadOnly();

    public int IndexOf(Guid songId) => _orderIndex.GetValueOrDefault(songId, -1);

    public int ShuffledIndexOf(Guid songId) => _shuffledIndex.GetValueOrDefault(songId, -1);

    public void SetOrder(List<Guid> songIds) => _order = songIds;

    public void SetShuffled(List<Guid> songIds) => _shuffled = songIds;

    public void GenerateShuffled()
    {
        _shuffled = new List<Guid>(_order);
        Random.Shared.Shuffle(CollectionsMarshal.AsSpan(_shuffled));
    }

    public void ClearShuffled() => _shuffled.Clear();

    public void Add(Guid songId) => _order.Add(songId);

    public void AddRange(IEnumerable<Guid> songIds) => _order.AddRange(songIds);

    public void RemoveAll(ISet<Guid> songIds)
    {
        _order.RemoveAll(songIds.Contains);
        _shuffled.RemoveAll(songIds.Contains);
    }

    public void Clear()
    {
        _order.Clear();
        _shuffled.Clear();
        _orderIndex.Clear();
        _shuffledIndex.Clear();
    }

    /// <summary>
    ///     Moves <paramref name="songId" /> to the front of the shuffled order.
    /// </summary>
    public void MoveToShuffledFront(Guid songId)
    {
        _shuffled.Remove(songId);
        _shuffled.Insert(0, songId);
    }

    /// <summary>
    ///     Moves <paramref name="songId" /> before <paramref name="beforeSongId" />, or to the end when it
    ///     is null, in the shuffled order when <paramref name="inShuffled" /> is set.
    /// </summary>
    /// <returns>Whether anything moved.</returns>
    public bool Move(Guid songId, Guid? beforeSongId, bool inShuffled)
    {
        var queue = inShuffled ? _shuffled : _order;
        var from = queue.IndexOf(songId);
        var target = beforeSongId.HasValue ? queue.IndexOf(beforeSongId.Value) : queue.Count;
        if (from < 0 || target < 0 || from == target || from + 1 == target) return false;

        queue.RemoveAt(from);
        queue.Insert(target > from ? target - 1 : target, songId);
        return true;
    }

    /// <summary>
    ///     Places <paramref name="songId" /> right after the current track, or after
    ///     <paramref name="stoppedIndex" /> when nothing is playing.
    /// </summary>
    public void MoveAfterCurrent(Guid songId, Guid? currentTrackId, int stoppedIndex, bool inShuffled)
    {
        var queue = inShuffled ? _shuffled : _order;
        var currentIndex = currentTrackId is null ? stoppedIndex : queue.IndexOf(currentTrackId.Value);
        var removedIndex = queue.IndexOf(songId);
        if (removedIndex >= 0)
        {
            queue.RemoveAt(removedIndex);
            if (removedIndex < currentIndex) currentIndex--;
        }

        queue.Insert(Math.Clamp(currentIndex + 1, 0, queue.Count), songId);
    }

    /// <summary>
    ///     Drops shuffled entries no longer in the play order and appends newly queued songs to the
    ///     shuffled order in random order, then rebuilds the shuffled index.
    /// </summary>
    public void SyncShuffledWithOrder()
    {
        var retained = _order.ToHashSet();
        _shuffled.RemoveAll(id => !retained.Contains(id));
        retained.ExceptWith(_shuffled);
        var additions = retained.ToArray();
        Random.Shared.Shuffle(additions);
        _shuffled.AddRange(additions);
        RebuildShuffledIndex();
    }

    public void RebuildIndex() => Rebuild(_orderIndex, _order);

    public void RebuildShuffledIndex() => Rebuild(_shuffledIndex, _shuffled);

    /// <summary>
    ///     The play-order index of the track before or after <paramref name="originalIndex" />, following
    ///     the shuffled order when shuffling and wrapping around when <paramref name="repeatAll" /> is set.
    /// </summary>
    /// <returns>The play-order index, or -1 at either end of a non-repeating queue.</returns>
    public int GetAdjacentIndex(int originalIndex, bool moveForward, bool shuffle, bool repeatAll)
    {
        if (originalIndex < 0 || originalIndex >= _order.Count) return -1;
        var queue = shuffle ? _shuffled : _order;
        var index = shuffle ? ShuffledIndexOf(_order[originalIndex]) : originalIndex;
        if (index < 0) return -1;

        var nextIndex = index + (moveForward ? 1 : -1);
        if (nextIndex < 0 || nextIndex >= queue.Count)
        {
            if (!repeatAll) return -1;
            nextIndex = moveForward ? 0 : queue.Count - 1;
        }

        return shuffle ? IndexOf(queue[nextIndex]) : nextIndex;
    }

    /// <summary>
    ///     The first song after <paramref name="currentIndex" /> (in the active order) that is not about to
    ///     be removed, wrapping around when <paramref name="repeatAll" /> is set.
    /// </summary>
    public Guid? GetNextSurvivingSongId(ISet<Guid> songIdsToRemove, int currentIndex, bool shuffle, bool repeatAll)
    {
        var activeQueue = shuffle ? _shuffled : _order;
        if (currentIndex < 0 || currentIndex >= activeQueue.Count) return null;

        for (var i = currentIndex + 1; i < activeQueue.Count; i++)
        {
            if (!songIdsToRemove.Contains(activeQueue[i])) return activeQueue[i];
        }

        if (!repeatAll) return null;

        for (var i = 0; i < currentIndex; i++)
        {
            if (!songIdsToRemove.Contains(activeQueue[i])) return activeQueue[i];
        }

        return null;
    }

    private static void Rebuild(Dictionary<Guid, int> index, List<Guid> queue)
    {
        index.Clear();
        for (var i = 0; i < queue.Count; i++) index[queue[i]] = i;
    }
}

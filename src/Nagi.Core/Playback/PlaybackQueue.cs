using System.Collections.ObjectModel;
using System.Runtime.InteropServices;

namespace Nagi.Core.Playback;

/// <summary>
///     The songs queued to play, in their own order and, while shuffling, in a shuffled play order, with the current
///     position in them. Every edit keeps the reverse index, used for O(1) lookups in very large queues, up to date.
/// </summary>
/// <remarks>Not thread-safe: <see cref="MusicPlaybackService" /> serialises access.</remarks>
internal sealed class PlaybackQueue
{
    private List<Guid> _order = [];
    private List<Guid>? _shuffled;
    private readonly Dictionary<Guid, int> _positions = new();

    /// <summary>
    ///     The queue in the order it was built. Replacing the queue starts a new list, so an earlier view keeps
    ///     showing the old one.
    /// </summary>
    public ReadOnlyCollection<Guid> Order => _order.AsReadOnly();

    /// <summary>The queue in the order it plays: <see cref="Order" />, or its shuffled order while shuffling.</summary>
    public ReadOnlyCollection<Guid> PlayOrder => ActiveOrder.AsReadOnly();

    public bool IsShuffled => _shuffled is not null;

    public int Count => _order.Count;

    /// <summary>
    ///     The song at the current position: the one playing, or the one playback resumes with while stopped.
    ///     Null only while the queue is empty or nothing has been chosen yet.
    /// </summary>
    public Guid? CurrentId { get; private set; }

    /// <summary>The position of <see cref="CurrentId" /> in <see cref="PlayOrder" />, or -1.</summary>
    public int CurrentPosition => CurrentId is { } id ? PositionOf(id) : -1;

    private List<Guid> ActiveOrder => _shuffled ?? _order;

    public int PositionOf(Guid songId) => _positions.GetValueOrDefault(songId, -1);

    public bool Contains(Guid songId) => _positions.ContainsKey(songId);

    /// <summary>
    ///     Replaces the queue with <paramref name="songIds" />, which must be distinct, and makes
    ///     <paramref name="startId" /> current. While shuffling it plays first, followed by the rest in
    ///     <paramref name="shuffledOrder" /> or a random order; without one, the shuffled order decides what plays first.
    /// </summary>
    public void Replace(List<Guid> songIds, bool shuffle, Guid? startId, IReadOnlyList<Guid>? shuffledOrder = null)
    {
        _order = songIds;
        _shuffled = shuffle ? shuffledOrder?.ToList() ?? Shuffle(_order) : null;
        if (_shuffled is not null && startId is { } first)
        {
            _shuffled.Remove(first);
            _shuffled.Insert(0, first);
        }
        CurrentId = _shuffled is not null ? _shuffled.FirstOrDefault() : startId ?? _order.FirstOrDefault();
        if (CurrentId == Guid.Empty) CurrentId = null;
        Reindex();
    }

    /// <summary>Turns shuffling on with a new random order, or off. The current song stays current.</summary>
    public void SetShuffled(bool shuffle)
    {
        _shuffled = shuffle ? Shuffle(_order) : null;
        Reindex();
    }

    /// <param name="songId">A queued song, or null to have nothing current.</param>
    public void SetCurrent(Guid? songId)
    {
        if (songId is { } id && !Contains(id)) throw new ArgumentException("The song isn't queued.", nameof(songId));
        CurrentId = songId;
    }

    /// <summary>Appends the songs not queued yet; while shuffling they join the shuffled order in random order.</summary>
    /// <returns>Whether anything was added.</returns>
    public bool Add(IEnumerable<Guid> songIds)
    {
        var added = songIds.Where(id => id != Guid.Empty).Distinct().Where(id => !Contains(id)).ToList();
        if (added.Count == 0) return false;
        _order.AddRange(added);
        _shuffled?.AddRange(Shuffle(added));
        Reindex();
        return true;
    }

    /// <summary>
    ///     Removes songs. When the current song goes, the next remaining song in play order, or else the last one,
    ///     becomes current.
    /// </summary>
    public void Remove(IReadOnlySet<Guid> songIds)
    {
        if (CurrentId is { } current && songIds.Contains(current))
            CurrentId = FindFromCurrent(id => !songIds.Contains(id), wrap: false)
                        ?? ActiveOrder.LastOrDefault(id => !songIds.Contains(id));
        if (CurrentId == Guid.Empty) CurrentId = null;
        _order.RemoveAll(songIds.Contains);
        _shuffled?.RemoveAll(songIds.Contains);
        Reindex();
    }

    public void Clear()
    {
        _order = [];
        if (_shuffled is not null) _shuffled = [];
        CurrentId = null;
        _positions.Clear();
    }

    /// <summary>
    ///     Moves <paramref name="songId" /> in play order before <paramref name="beforeSongId" />, or to the end when it
    ///     is null.
    /// </summary>
    /// <returns>Whether anything moved.</returns>
    public bool Move(Guid songId, Guid? beforeSongId)
    {
        var queue = ActiveOrder;
        var from = PositionOf(songId);
        var target = beforeSongId is { } before ? PositionOf(before) : queue.Count;
        if (from < 0 || target < 0 || from == target || from + 1 == target) return false;

        queue.RemoveAt(from);
        queue.Insert(target > from ? target - 1 : target, songId);
        Reindex();
        return true;
    }

    /// <summary>
    ///     Places the songs, in the order given, right after the current song in both orders, queueing any that
    ///     aren't. The current song itself stays where it is.
    /// </summary>
    public void MoveAfterCurrent(IReadOnlyList<Guid> songIds)
    {
        var moved = songIds.Where(id => id != Guid.Empty && id != CurrentId).Distinct().ToList();
        if (moved.Count == 0) return;
        InsertAfterCurrent(_order, moved);
        if (_shuffled is not null) InsertAfterCurrent(_shuffled, moved);
        Reindex();
    }

    /// <summary>
    ///     The song before or after <paramref name="from" />, or the current one, in play order, wrapping around when
    ///     asked.
    /// </summary>
    public Guid? GetAdjacent(bool forward, bool wrap, Guid? from = null)
    {
        var position = from is { } songId ? PositionOf(songId) : CurrentPosition;
        if (position < 0) return null;
        var queue = ActiveOrder;
        var next = position + (forward ? 1 : -1);
        if (next < 0 || next >= queue.Count)
        {
            if (!wrap) return null;
            next = forward ? 0 : queue.Count - 1;
        }
        return queue[next];
    }

    /// <summary>The first song after the current one in play order that <paramref name="match" /> accepts.</summary>
    public Guid? FindFromCurrent(Func<Guid, bool> match, bool wrap)
    {
        var position = CurrentPosition;
        if (position < 0) return null;
        var queue = ActiveOrder;
        for (var i = position + 1; i < queue.Count; i++)
            if (match(queue[i])) return queue[i];
        if (!wrap) return null;
        for (var i = 0; i < position; i++)
            if (match(queue[i])) return queue[i];
        return null;
    }

    /// <summary>
    ///     The play order from the current song, or from the start when there is none, continuing from the start
    ///     when <paramref name="wrap" /> is set.
    /// </summary>
    public Guid[] Upcoming(bool wrap)
    {
        var queue = ActiveOrder;
        var start = Math.Max(0, CurrentPosition);
        return wrap ? [.. queue.Skip(start), .. queue.Take(start)] : [.. queue.Skip(start)];
    }

    private void InsertAfterCurrent(List<Guid> queue, List<Guid> songIds)
    {
        var moving = songIds.ToHashSet();
        queue.RemoveAll(moving.Contains);
        var current = CurrentId is { } id ? queue.IndexOf(id) : -1;
        queue.InsertRange(current + 1, songIds);
    }

    private static List<Guid> Shuffle(List<Guid> songIds)
    {
        var shuffled = new List<Guid>(songIds);
        Random.Shared.Shuffle(CollectionsMarshal.AsSpan(shuffled));
        return shuffled;
    }

    private void Reindex()
    {
        _positions.Clear();
        var queue = ActiveOrder;
        for (var i = 0; i < queue.Count; i++) _positions[queue[i]] = i;
        if (CurrentId is { } id && !_positions.ContainsKey(id)) CurrentId = null;
    }
}

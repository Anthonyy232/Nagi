using System.Diagnostics.CodeAnalysis;
using Nagi.Core.Models;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Songs played from files outside the library, looked up by id or by file path.
///     They live only as long as they are queued or playing.
/// </summary>
internal sealed class TransientTrackCache
{
    private readonly Dictionary<Guid, Song> _tracks = new();
    private readonly Dictionary<string, Guid> _idsByPath = new(StringComparer.OrdinalIgnoreCase);

    public Song this[Guid songId] => _tracks[songId];

    public bool ContainsKey(Guid songId) => _tracks.ContainsKey(songId);

    public bool TryGetValue(Guid songId, [MaybeNullWhen(false)] out Song song) => _tracks.TryGetValue(songId, out song);

    public Song? GetValueOrDefault(Guid songId) => _tracks.GetValueOrDefault(songId);

    public bool TryGetByPath(string filePath, [MaybeNullWhen(false)] out Song song)
    {
        if (_idsByPath.TryGetValue(filePath, out var songId)) return _tracks.TryGetValue(songId, out song);
        song = null;
        return false;
    }

    public void Register(Song song)
    {
        _tracks[song.Id] = song;
        _idsByPath[song.FilePath] = song.Id;
    }

    public void RetainOnly(IReadOnlySet<Guid> retainedIds)
    {
        foreach (var id in _tracks.Keys.Where(id => !retainedIds.Contains(id)).ToList())
        {
            _idsByPath.Remove(_tracks[id].FilePath);
            _tracks.Remove(id);
        }
    }

    public void Clear()
    {
        _tracks.Clear();
        _idsByPath.Clear();
    }
}

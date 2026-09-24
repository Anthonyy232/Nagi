using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Nagi.Core.Models;

namespace Nagi.WinUI.Models;

public sealed class QueueEntry(Song song, int position) : ObservableObject
{
    private int _position = position;
    public Song Song { get; } = song;
    public Guid Id => Song.Id;
    public int Position
    {
        get => _position;
        set => SetProperty(ref _position, value);
    }
    public string Title => Song.Title;
    public string ArtistName => Song.ArtistName;
    public string GenreText { get; } = string.Join(", ", song.Genres.Select(g => g.Name).OrderBy(name => name));
    public TimeSpan Duration => Song.Duration;
    public override string ToString() => $"{Title} — {ArtistName}";
}

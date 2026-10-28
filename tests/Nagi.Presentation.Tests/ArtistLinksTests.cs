using FluentAssertions;
using Nagi.Core.Models;
using Nagi.Presentation.Helpers;
using Xunit;

namespace Nagi.Presentation.Tests;

public class ArtistLinksTests
{
    [Fact]
    public void ArtistNames_KeepDenormalizedNameWhenRelationshipsAreNotLoaded_AndNavigateFromTheSong()
    {
        var song = new Song { SongArtists = [new SongArtist { ArtistId = Guid.NewGuid() }] };

        ArtistLinks.GetNames(song, null, "Earth, Wind & Fire")
            .Should().Equal("Earth, Wind & Fire");
        ArtistLinks.ShowsStoredSongName(song, null).Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArtistNames_UseOrderedRelationshipsEvenBeforeDisplayNameArrives(bool album)
    {
        var first = new Artist { Name = "First" };
        var second = new Artist { Name = "Second" };
        var blank = new Artist { Name = " " };
        var song = new Song
        {
            SongArtists =
            [
                new SongArtist { Artist = second, Order = 2 },
                new SongArtist { Artist = blank, Order = 0 },
                new SongArtist { Artist = first, Order = 1 }
            ]
        };
        AlbumArtist[] albumArtists =
        [
            new() { Artist = second, Order = 2 },
            new() { Artist = blank, Order = 0 },
            new() { Artist = first, Order = 1 }
        ];

        ArtistLinks.GetNames(album ? null : song, album ? albumArtists : null, null)
            .Should().Equal("First", "Second");
    }
}

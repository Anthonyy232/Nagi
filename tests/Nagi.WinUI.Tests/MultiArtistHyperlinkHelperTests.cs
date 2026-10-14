using FluentAssertions;
using Nagi.Core.Models;
using Nagi.WinUI.Helpers;
using Xunit;

namespace Nagi.WinUI.Tests;

public class MultiArtistHyperlinkHelperTests
{
    [Fact]
    public void ArtistNames_KeepDenormalizedNameWhenRelationshipsAreNotLoaded()
    {
        var song = new Song { SongArtists = [new SongArtist { ArtistId = Guid.NewGuid() }] };

        MultiArtistHyperlinkHelper.GetArtistNames(song, null, "Earth, Wind & Fire")
            .Should().Equal("Earth, Wind & Fire");
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

        MultiArtistHyperlinkHelper.GetArtistNames(album ? null : song, album ? albumArtists : null, null)
            .Should().Equal("First", "Second");
    }
}

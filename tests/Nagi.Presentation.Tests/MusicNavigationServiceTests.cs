using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.Services.Implementations;
using NSubstitute;
using Xunit;

namespace Nagi.Presentation.Tests;

public class MusicNavigationServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArtistLinksWithoutLoadedArtists_ResolvePrimaryOrRequestedArtist(bool namedRequest)
    {
        var primary = new Artist { Name = "Primary" };
        var guest = new Artist { Name = "Guest" };
        var song = new Song { SongArtists = [new SongArtist { ArtistId = primary.Id }] };
        var library = Substitute.For<ILibraryReader>();
        library.GetArtistsForSongAsync(song.Id).Returns(new[] { primary, guest });
        var navigation = Substitute.For<INavigationService>();
        var service = new MusicNavigationService(library, navigation, NullLogger<MusicNavigationService>.Instance);

        await service.NavigateToArtistAsync(namedRequest
            ? new ArtistNavigationRequest { Song = song, ArtistName = "guest" }
            : song);

        var expectedId = namedRequest ? guest.Id : primary.Id;
        navigation.Received(1).NavigateTo(new ArtistViewNavigationParameter(expectedId));
        await library.DidNotReceive().GetSongByIdAsync(Arg.Any<Guid>());
    }

    [Fact]
    public async Task NamedArtistMissingFromSong_UsesGlobalLookupRatherThanPrimaryArtist()
    {
        var primary = new Artist { Name = "Primary" };
        var requested = new Artist { Name = "Requested" };
        var song = new Song { SongArtists = [new SongArtist { Artist = primary }] };
        var library = Substitute.For<ILibraryReader>();
        library.GetArtistByNameAsync(requested.Name).Returns(requested);
        var navigation = Substitute.For<INavigationService>();
        var service = new MusicNavigationService(library, navigation, NullLogger<MusicNavigationService>.Instance);

        await service.NavigateToArtistAsync(new ArtistNavigationRequest { Song = song, ArtistName = requested.Name });

        navigation.Received(1).NavigateTo(new ArtistViewNavigationParameter(requested.Id));
        await library.DidNotReceive().GetArtistsForSongAsync(Arg.Any<Guid>());
    }

    [Fact]
    public async Task SongWithAlbumId_DoesNotReloadSongBeforeNavigation()
    {
        var song = new Song { AlbumId = Guid.NewGuid() };
        var library = Substitute.For<ILibraryReader>();
        var navigation = Substitute.For<INavigationService>();
        var service = new MusicNavigationService(library, navigation, NullLogger<MusicNavigationService>.Instance);

        await service.NavigateToAlbumAsync(song);

        navigation.Received(1).NavigateTo(new AlbumViewNavigationParameter(song.AlbumId.Value));
        await library.DidNotReceive().GetSongByIdAsync(Arg.Any<Guid>());
    }
}

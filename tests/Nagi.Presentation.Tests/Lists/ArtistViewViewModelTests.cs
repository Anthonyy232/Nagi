using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Library;
using Nagi.Core.Playback;
using Nagi.Core.Playlists;
using Nagi.Presentation.Common;
using Nagi.Presentation.Lists;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Presentation.Tests.Lists;

public class ArtistViewViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataCompletesAfterLeaving_DoesNotUpdateTheDisposedView(bool fails)
    {
        var library = new LibraryFakes();
        var metadata = new TaskCompletionSource<Artist?>(TaskCreationOptions.RunContinuationsAsynchronously);
        library.Artwork.GetArtistDetailsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(metadata.Task);
        using var viewModel = CreateViewModel(library);
        var originalName = viewModel.PageTitle;
        var load = viewModel.OpenAsync(new ArtistViewNavigationParameter(Guid.NewGuid()));

        viewModel.ResetState();
        viewModel.Dispose();
        if (fails) metadata.SetException(new IOException("Old artist request failed"));
        else metadata.SetResult(new Artist { Name = "Old artist" });
        await load.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.PageTitle.Should().Be(originalName);
        viewModel.Albums.Should().BeEmpty();
    }

    [Fact]
    public async Task Rescan_ReloadsTheArtistsSongsAndAlbums()
    {
        var library = new LibraryFakes();
        var artistId = Guid.NewGuid();
        library.Artwork.GetArtistDetailsAsync(artistId, Arg.Any<CancellationToken>()).Returns(new Artist { Id = artistId, Name = "Artist" });
        Album[] albums = [new Album { Title = "First" }];
        library.Reader.GetAlbumsForArtistAsync(artistId).Returns(_ => albums);
        using var viewModel = CreateViewModel(library);
        await viewModel.OpenAsync(new ArtistViewNavigationParameter(artistId));
        viewModel.Albums.Select(album => album.Name).Should().Equal("First");
        library.Reader.ClearReceivedCalls();
        var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.Albums.CollectionChanged += (_, _) => reloaded.TrySetResult();

        albums = [albums[0], new Album { Title = "Second" }];
        library.Notifier.Changed += Raise.Event<EventHandler<LibraryChange>>(
            library.Notifier, new LibraryChange(LibraryChangeKind.FolderRescanned, Guid.NewGuid()));
        await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.Albums.Select(album => album.Name).Should().Equal("First", "Second");
        await library.Reader.Received().GetSongsAsync(new SongScope.Artist(artistId), Arg.Any<string?>(), Arg.Any<SongSortOrder>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangeArtistImage_CancelledPickerLeavesImageUntouched()
    {
        var library = new LibraryFakes();
        var ui = Substitute.For<IUIService>();
        ui.PickSingleFileAsync(Arg.Any<IEnumerable<string>>()).Returns((string?)null);
        using var viewModel = CreateViewModel(library, ui);

        await viewModel.ChangeArtistImageCommand.ExecuteAsync(null);

        await library.Artwork.DidNotReceiveWithAnyArgs().UpdateArtistImageAsync(default, default!);
    }

    private static ArtistViewViewModel CreateViewModel(LibraryFakes library, IUIService? ui = null)
    {
        library.Reader.GetSongsAsync(Arg.Any<SongScope>(), Arg.Any<string?>(), Arg.Any<SongSortOrder>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new PagedResult<Song>
            {
                PageNumber = 1,
                PageSize = 50,
                Items = []
            });
        var settings = TestSettings.Create();
        return new ArtistViewViewModel(library.Reader, library.Listening, library.Notifier, library.Artwork, Substitute.For<IPlaylistService>(),
            Substitute.For<IMusicPlaybackService>(), Substitute.For<IMusicNavigationService>(),
            settings, new ImmediateDispatcher(), ui ?? Substitute.For<IUIService>(), NullLogger<ArtistViewViewModel>.Instance);
    }
}

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.WinUI.Services.Abstractions;
using Nagi.WinUI.ViewModels;
using NSubstitute;
using Xunit;

namespace Nagi.WinUI.Tests;

public class ArtistViewViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataCompletesAfterLeaving_DoesNotUpdateTheDisposedView(bool fails)
    {
        var library = new LibraryFakes();
        var scanner = Substitute.For<ILibraryScanner>();
        var metadata = new TaskCompletionSource<Artist?>(TaskCreationOptions.RunContinuationsAsynchronously);
        scanner.GetArtistDetailsAsync(Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(metadata.Task);
        using var viewModel = CreateViewModel(library, scanner);
        var originalName = viewModel.ArtistName;
        var load = viewModel.LoadArtistDetailsAsync(Guid.NewGuid());

        viewModel.ResetState();
        viewModel.Dispose();
        if (fails) metadata.SetException(new IOException("Old artist request failed"));
        else metadata.SetResult(new Artist { Name = "Old artist" });
        await load.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.ArtistName.Should().Be(originalName);
        viewModel.Albums.Should().BeEmpty();
    }

    [Fact]
    public async Task ImageCommands_UseTheLibraryWriterWhenScannerIsASeparateService()
    {
        var library = new LibraryFakes();
        var scanner = Substitute.For<ILibraryScanner>();
        var artistId = Guid.NewGuid();
        scanner.GetArtistDetailsAsync(artistId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new Artist { Id = artistId, Name = "Artist" });
        using var viewModel = CreateViewModel(library, scanner);
        await viewModel.LoadArtistDetailsAsync(artistId);

        await viewModel.UpdateArtistImageAsync("portrait.png");
        await viewModel.RemoveArtistImageAsync();

        await library.Writer.Received(1).UpdateArtistImageAsync(artistId, "portrait.png");
        await library.Writer.Received(1).RemoveArtistImageAsync(artistId);
    }

    private static ArtistViewViewModel CreateViewModel(LibraryFakes library, ILibraryScanner scanner)
    {
        library.Reader.GetSongsByArtistIdPagedAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<SongSortOrder>(), Arg.Any<CancellationToken>()).Returns(new PagedResult<Song>
            {
                PageNumber = 1, PageSize = 50, Items = []
            });
        var settings = Substitute.For<IUISettingsService>();
        settings.GetSongsPerPageAsync().Returns(50);
        return new ArtistViewViewModel(library.Reader, library.Writer, scanner, Substitute.For<IPlaylistService>(),
            Substitute.For<IMusicPlaybackService>(), Substitute.For<IMusicNavigationService>(),
            settings, new ImmediateDispatcher(), Substitute.For<IUIService>(), NullLogger<ArtistViewViewModel>.Instance);
    }
}

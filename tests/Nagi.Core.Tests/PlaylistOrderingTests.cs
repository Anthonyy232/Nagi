using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

public class PlaylistOrderingTests : IDisposable
{
    private readonly DbContextFactoryTestHelper _dbHelper;
    private readonly LibraryService _libraryService;
    private readonly LibraryReader _libraryReader;
    private readonly PlaylistService _playlistService;
    private readonly ProviderPipelineProvider _pipelines;

    public PlaylistOrderingTests()
    {
        _dbHelper = new DbContextFactoryTestHelper();
        _pipelines = TestProviderPipeline.Build(ServiceProviderIds.ImageDownload);

        _libraryReader = new LibraryReader(_dbHelper.ContextFactory, Substitute.For<IFileSystemService>(), Substitute.For<ISettingsService>(), Substitute.For<ILogger<LibraryReader>>());
        _playlistService = new PlaylistService(_dbHelper.ContextFactory, Substitute.For<IFileSystemService>(), Substitute.For<IImageProcessor>(), Substitute.For<IPathConfiguration>(), Substitute.For<ILogger<PlaylistService>>());

        _libraryService = new LibraryService(
            _dbHelper.ContextFactory,
            _libraryReader,
            Substitute.For<IFileSystemService>(),
            Substitute.For<IMetadataService>(),
            Substitute.For<ILastFmMetadataService>(),
            Substitute.For<IMusicBrainzService>(),
            Substitute.For<IFanartTvService>(),
            Substitute.For<ITheAudioDbService>(),
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<IPathConfiguration>(),
            Substitute.For<ISettingsService>(),
            Substitute.For<IReplayGainService>(),
            Substitute.For<IImageProcessor>(),
            _pipelines,
            Substitute.For<ILogger<LibraryService>>());
    }

    public void Dispose()
    {
        _libraryService.Dispose();
        _libraryReader.Dispose();
        _pipelines.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _dbHelper.Dispose();
    }

    [Fact]
    public async Task RenamePlaylistAsync_EmitsPersistedModificationDate()
    {
        var playlist = await _playlistService.CreatePlaylistAsync("Original name");
        PlaylistUpdatedEventArgs? update = null;
        _playlistService.PlaylistUpdated += (_, args) => update = args;

        var success = await _playlistService.RenamePlaylistAsync(playlist!.Id, "New name");

        success.Should().BeTrue();
        var persisted = await _libraryReader.GetPlaylistByIdAsync(playlist.Id);
        update.Should().NotBeNull();
        update!.PlaylistId.Should().Be(playlist.Id);
        update.DateModified.Should().Be(persisted!.DateModified);
    }

    [Fact]
    public async Task AddSongsToPlaylistAsync_AppendsUniqueSongsAfterExistingFractionalOrders()
    {
        var (playlist, songs) = await SeedPlaylistAsync(0.5, 1.5, 2.5);
        await _playlistService.RemoveSongsFromPlaylistAsync(playlist.Id, [songs[1].Id, songs[2].Id]);

        await _playlistService.AddSongsToPlaylistAsync(playlist.Id, [songs[0].Id, songs[2].Id, songs[2].Id, songs[1].Id]);

        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        var entries = await context.PlaylistSongs.OrderBy(x => x.Order).ToListAsync();
        entries.Select(x => x.SongId).Should().Equal(songs[0].Id, songs[2].Id, songs[1].Id);
        entries.Select(x => x.Order).Should().Equal(0.5, 1, 2);
    }

    [Fact]
    public async Task MovePlaylistSongAsync_PersistsFractionalOrderAndModificationDate()
    {
        var (playlist, songs) = await SeedPlaylistAsync(1, 2, 3);

        var result = await _playlistService.MovePlaylistSongAsync(playlist.Id, songs[2].Id, 1.5);

        result.Should().BeTrue();
        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        var entries = await context.PlaylistSongs.OrderBy(x => x.Order).ToListAsync();
        entries.Select(x => x.SongId).Should().Equal(songs[0].Id, songs[2].Id, songs[1].Id);
        entries.Select(x => x.Order).Should().Equal(1, 1.5, 2);
        (await context.Playlists.FindAsync(playlist.Id))!.DateModified.Should().BeAfter(playlist.DateModified);
    }

    [Fact]
    public async Task UpdatePlaylistOrderAsync_NormalizesEvenNearIntegerValues()
    {
        var (playlist, songs) = await SeedPlaylistAsync(1.0 + 1e-12, 1.5, 1.6);

        await _playlistService.UpdatePlaylistOrderAsync(playlist.Id, songs.Select(s => s.Id));

        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        var entries = await context.PlaylistSongs.OrderBy(x => x.Order).ToListAsync();
        entries.Select(x => x.SongId).Should().Equal(songs.Select(s => s.Id));
        entries.Select(x => x.Order).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task PlaylistReads_PreserveCustomOrderAcrossExportPagingAndSearch()
    {
        var (playlist, songs) = await SeedPlaylistAsync(3, 1, 2);
        var expectedIds = new[] { songs[1].Id, songs[2].Id, songs[0].Id };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var other = new Song { Title = "Other", FolderId = songs[0].FolderId, FilePath = @"C:\other.mp3" };
            context.PlaylistSongs.Add(new PlaylistSong { PlaylistId = playlist.Id, Song = other, Order = 1.25 });
            await context.SaveChangesAsync();
        }

        var exported = await _libraryReader.GetSongsInPlaylistOrderedAsync(playlist.Id);
        exported.Where(s => s.Title.StartsWith("Song")).Select(s => s.Id).Should().Equal(expectedIds);

        var page = await _libraryReader.GetSongsByPlaylistPagedAsync(playlist.Id, 3, 1, SongSortOrder.PlaylistOrder);
        page.TotalCount.Should().Be(4);
        var row = page.Items.Should().ContainSingle().Subject;
        row.Id.Should().Be(songs[2].Id);
        row.Order.Should().Be(2);
        row.Grouping.Should().Be("Group");
        row.Lyrics.Should().BeNull();

        var search = await _libraryReader.SearchSongsInPlaylistPagedAsync(playlist.Id, "Song", 2, 1, SongSortOrder.PlaylistOrder);
        search.TotalCount.Should().Be(3);
        search.Items.Should().ContainSingle().Which.Id.Should().Be(songs[2].Id);
        search.Items.Single().Order.Should().Be(2);
        var ids = await _libraryReader.SearchAllSongIdsInPlaylistAsync(playlist.Id, "Song", SongSortOrder.PlaylistOrder);
        ids.Should().Equal(expectedIds);
    }

    private async Task<(Playlist Playlist, Song[] Songs)> SeedPlaylistAsync(params double[] orders)
    {
        var folder = new Folder { Name = "Music", Path = @"C:\Music" };
        var songs = orders.Select((_, i) => new Song
        {
            Title = $"Song {i}", FilePath = $@"C:\Music\{i}.mp3", DirectoryPath = folder.Path,
            Folder = folder, TrackNumber = orders.Length - i, Lyrics = "Heavy lyrics", Grouping = "Group"
        }).ToArray();
        var playlist = new Playlist { Name = "Test", DateModified = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        context.PlaylistSongs.AddRange(songs.Select((song, i) => new PlaylistSong { Playlist = playlist, Song = song, Order = orders[i] }));
        await context.SaveChangesAsync();
        return (playlist, songs);
    }
}

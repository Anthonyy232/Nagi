using FluentAssertions;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Implementations;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

public class M3uPlaylistExportServiceTests : IDisposable
{
    private readonly string _tempDirectory = Directory.CreateTempSubdirectory("Nagi-M3uTests-").FullName;
    private readonly ILibraryReader _libraryReader;
    private readonly IPlaylistService _playlistService;
    private readonly ILogger<M3uPlaylistExportService> _logger;
    private readonly M3uPlaylistExportService _service;

    public M3uPlaylistExportServiceTests()
    {
        _libraryReader = Substitute.For<ILibraryReader>();
        _playlistService = Substitute.For<IPlaylistService>();
        _playlistService.AddSongsToPlaylistAsync(Arg.Any<Guid>(), Arg.Any<IEnumerable<Guid>>()).Returns(true);
        _logger = Substitute.For<ILogger<M3uPlaylistExportService>>();
        _service = new M3uPlaylistExportService(_libraryReader, _playlistService, _logger);
    }

    public void Dispose() => Directory.Delete(_tempDirectory, true);

    [Fact]
    public async Task ExportPlaylistAsync_WhenPlaylistNotFound_ReturnsFailureResult()
    {
        var playlistId = Guid.NewGuid();
        _libraryReader.GetPlaylistByIdAsync(playlistId).Returns((Playlist?)null);

        var result = await _service.ExportPlaylistAsync(playlistId, Path.Combine(_tempDirectory, "missing.m3u8"));

        result.Success.Should().BeFalse();
        result.SongCount.Should().Be(0);
    }

    [Fact]
    public async Task ExportPlaylistAsync_WritesExtm3uAndPlaylistHeaders()
    {
        var playlistId = Guid.NewGuid();
        var filePath = Path.Combine(_tempDirectory, "playlist.m3u8");
        var playlist = new Playlist { Id = playlistId, Name = "My Playlist" };
        _libraryReader.GetPlaylistByIdAsync(playlistId).Returns(playlist);
        _libraryReader.GetSongsInPlaylistOrderedAsync(playlistId).Returns(new List<Song>());

        await _service.ExportPlaylistAsync(playlistId, filePath);

        var content = await File.ReadAllTextAsync(filePath);
        content.Should().StartWith("#EXTM3U", "M3U files must begin with the #EXTM3U header");
        content.Should().Contain("#PLAYLIST:My Playlist", "the playlist name must appear in the #PLAYLIST directive");
    }

    [Fact]
    public async Task ExportPlaylistAsync_WritesExtinfLineWithDurationAndArtistTitle()
    {
        var playlistId = Guid.NewGuid();
        var filePath = Path.Combine(_tempDirectory, "playlist.m3u8");
        var playlist = new Playlist { Id = playlistId, Name = "P" };
        var song = new Song
        {
            Title = "Track One",
            FilePath = @"C:\music\track.mp3",
            DurationTicks = TimeSpan.FromSeconds(240).Ticks
        };
        var artist = new Artist { Name = "The Artist" };
        song.SongArtists.Add(new SongArtist { Song = song, Artist = artist, Order = 0 });
        song.SyncDenormalizedFields();

        _libraryReader.GetPlaylistByIdAsync(playlistId).Returns(playlist);
        _libraryReader.GetSongsInPlaylistOrderedAsync(playlistId).Returns(new List<Song> { song });

        var result = await _service.ExportPlaylistAsync(playlistId, filePath);

        result.Success.Should().BeTrue();
        var content = await File.ReadAllTextAsync(filePath);
        content.Should().Contain("#EXTINF:240,The Artist - Track One");
        content.Should().Contain(@"C:\music\track.mp3");
    }

    [Fact]
    public async Task ImportPlaylistAsync_WhenFileDoesNotExist_ReturnsFailureResult()
    {
        var result = await _service.ImportPlaylistAsync(Path.Combine(_tempDirectory, "missing.m3u"), "My Playlist");

        result.Success.Should().BeFalse();
        result.PlaylistId.Should().BeNull();
    }

    [Fact]
    public async Task ImportPlaylistAsync_WhenNoSongsMatchLibrary_ReturnsFailureWithUnmatchedPaths()
    {
        var m3uPath = Path.Combine(_tempDirectory, "playlist.m3u");
        await File.WriteAllTextAsync(m3uPath, $"#EXTM3U\nC:\\unknown\\song.mp3");
        _libraryReader.GetSongByFilePathAsync(Arg.Any<string>()).Returns((Song?)null);

        var result = await _service.ImportPlaylistAsync(m3uPath, "My Playlist");

        result.Success.Should().BeFalse("import should fail when nothing in the file matches the library");
        result.UnmatchedSongs.Should().Be(1);
        result.UnmatchedPaths.Should().Contain(@"C:\unknown\song.mp3");
    }

    [Fact]
    public async Task ImportPlaylistAsync_WithPartialLibraryMatch_ReturnsSuccessWithCorrectCounts()
    {
        var knownSong = new Song { Id = Guid.NewGuid(), FilePath = @"C:\music\known.mp3" };
        var newPlaylist = new Playlist { Id = Guid.NewGuid(), Name = "Test" };
        var m3uPath = Path.Combine(_tempDirectory, "playlist.m3u");
        await File.WriteAllLinesAsync(m3uPath, new[]
        {
            "#EXTM3U",
            @"C:\music\known.mp3",
            @"C:\music\missing.mp3"
        });
        _libraryReader.GetSongByFilePathAsync(@"C:\music\known.mp3").Returns(knownSong);
        _libraryReader.GetSongByFilePathAsync(@"C:\music\missing.mp3").Returns((Song?)null);
        _playlistService.CreatePlaylistAsync("Test").Returns(newPlaylist);

        var result = await _service.ImportPlaylistAsync(m3uPath, "Test");

        result.Success.Should().BeTrue();
        result.MatchedSongs.Should().Be(1);
        result.UnmatchedSongs.Should().Be(1);
        result.UnmatchedPaths.Should().Contain(@"C:\music\missing.mp3");
        await _playlistService.Received(1).AddSongsToPlaylistAsync(newPlaylist.Id, Arg.Is<IEnumerable<Guid>>(ids => ids != null && ids.Contains(knownSong.Id)));
        await _playlistService.DidNotReceive().DeletePlaylistAsync(Arg.Any<Guid>());
    }

    [Fact]
    public async Task ImportPlaylistAsync_SkipsM3uDirectivesAndBlankLines()
    {
        var matchedSong = new Song { Id = Guid.NewGuid(), FilePath = @"C:\music\track.mp3" };
        var newPlaylist = new Playlist { Id = Guid.NewGuid(), Name = "P" };
        var m3uPath = Path.Combine(_tempDirectory, "playlist.m3u");
        await File.WriteAllTextAsync(m3uPath,
            "#EXTM3U\n" +
            "#PLAYLIST:My Playlist\n" +
            "\n" +
            "#EXTINF:180,Artist - Track\n" +
            @"C:\music\track.mp3" + "\n" +
            "# A comment\n");
        _libraryReader.GetSongByFilePathAsync(@"C:\music\track.mp3").Returns(matchedSong);
        _playlistService.CreatePlaylistAsync(Arg.Any<string>()).Returns(newPlaylist);

        var result = await _service.ImportPlaylistAsync(m3uPath, "P");

        result.Success.Should().BeTrue();
        result.MatchedSongs.Should().Be(1, "only the one real file path should be matched");
        result.UnmatchedSongs.Should().Be(0, "comment and directive lines must not be treated as file paths");
    }

    [Fact]
    public async Task ImportPlaylistAsync_ResolvesRelativePathsAgainstM3uDirectory()
    {
        var tempDir = Path.Combine(_tempDirectory, "exports");
        Directory.CreateDirectory(tempDir);
        var m3uPath = Path.Combine(tempDir, "playlist.m3u");
        var relativeSongPath = "songs/track.mp3";
        var expectedAbsPath = Path.GetFullPath(Path.Combine(tempDir, relativeSongPath));

        await File.WriteAllTextAsync(m3uPath, $"#EXTM3U\n{relativeSongPath}");
        var matchedSong = new Song { Id = Guid.NewGuid(), FilePath = expectedAbsPath };
        var newPlaylist = new Playlist { Id = Guid.NewGuid(), Name = "playlist" };
        _libraryReader.GetSongByFilePathAsync(expectedAbsPath).Returns(matchedSong);
        _playlistService.CreatePlaylistAsync(Arg.Any<string>()).Returns(newPlaylist);

        var result = await _service.ImportPlaylistAsync(m3uPath, "playlist");

        result.Success.Should().BeTrue("relative path should be resolved to an absolute path for the library lookup");
        result.MatchedSongs.Should().Be(1);
    }

    [Fact]
    public async Task ExportAllPlaylistsAsync_WhenNoPlaylistsExist_ReturnsFailure()
    {
        var tempDir = Path.Combine(_tempDirectory, "exports");
        _libraryReader.GetAllPlaylistsAsync().Returns(Enumerable.Empty<Playlist>());

        var result = await _service.ExportAllPlaylistsAsync(tempDir);

        result.Success.Should().BeFalse();
        result.PlaylistsExported.Should().Be(0);
    }

    [Fact]
    public async Task ExportAllPlaylistsAsync_EmptyPlaylistsAreNotExported()
    {
        var tempDir = Path.Combine(_tempDirectory, "exports");
        var emptyPlaylist = new Playlist { Id = Guid.NewGuid(), Name = "Empty" };
        var filledPlaylist = new Playlist { Id = Guid.NewGuid(), Name = "Filled" };
        var song = new Song { Title = "S", FilePath = @"C:\music\s.mp3", DurationTicks = TimeSpan.FromMinutes(3).Ticks };
        song.SongArtists.Add(new SongArtist { Song = song, Artist = new Artist { Name = "A" }, Order = 0 });
        song.SyncDenormalizedFields();

        _libraryReader.GetAllPlaylistsAsync().Returns(new[] { emptyPlaylist, filledPlaylist });
        _libraryReader.GetSongsInPlaylistOrderedAsync(emptyPlaylist.Id).Returns(Enumerable.Empty<Song>());
        _libraryReader.GetSongsInPlaylistOrderedAsync(filledPlaylist.Id).Returns(new List<Song> { song });

        var result = await _service.ExportAllPlaylistsAsync(tempDir);

        result.Success.Should().BeTrue();
        result.PlaylistsExported.Should().Be(1, "the empty playlist should be skipped");
        result.TotalSongs.Should().Be(1);
        await _libraryReader.Received(1).GetSongsInPlaylistOrderedAsync(filledPlaylist.Id);
        await _libraryReader.DidNotReceive().GetPlaylistByIdAsync(filledPlaylist.Id);
    }

    [Fact]
    public async Task ExportAllPlaylistsAsync_WhenNamesSanitizeToSameFile_PreservesBothPlaylists()
    {
        var tempDir = Path.Combine(_tempDirectory, "exports");
        var first = new Playlist { Id = Guid.NewGuid(), Name = "Road/Trip" };
        var second = new Playlist { Id = Guid.NewGuid(), Name = "Road\\Trip" };
        var firstSong = CreateSong("First song", @"C:\music\first.mp3");
        var secondSong = CreateSong("Second song", @"C:\music\second.mp3");

        _libraryReader.GetAllPlaylistsAsync().Returns(new[] { first, second });
        _libraryReader.GetSongsInPlaylistOrderedAsync(first.Id).Returns(new[] { firstSong });
        _libraryReader.GetSongsInPlaylistOrderedAsync(second.Id).Returns(new[] { secondSong });

        var result = await _service.ExportAllPlaylistsAsync(tempDir);

        result.PlaylistsExported.Should().Be(2);
        var exportedFiles = Directory.GetFiles(tempDir, "*.m3u8");
        exportedFiles.Should().HaveCount(2);
        var contents = await Task.WhenAll(exportedFiles.Select(path => File.ReadAllTextAsync(path)));
        contents.Should().Contain(content => content.Contains("#PLAYLIST:Road/Trip"));
        contents.Should().Contain(content => content.Contains("#PLAYLIST:Road\\Trip"));
    }

    [Fact]
    public async Task ImportMultiplePlaylistsAsync_AggregatesResultsAcrossAllFiles()
    {
        var tempDir = Path.Combine(_tempDirectory, "exports");
        Directory.CreateDirectory(tempDir);
        var file1 = Path.Combine(tempDir, "playlist1.m3u");
        var file2 = Path.Combine(tempDir, "playlist2.m3u");
        await File.WriteAllLinesAsync(file1, new[] { @"C:\music\song1.mp3", @"C:\music\missing.mp3" });
        await File.WriteAllTextAsync(file2, @"C:\music\unknown.mp3");

        var song1 = new Song { Id = Guid.NewGuid(), FilePath = @"C:\music\song1.mp3" };
        var playlist1 = new Playlist { Id = Guid.NewGuid(), Name = "playlist1" };
        _libraryReader.GetSongByFilePathAsync(@"C:\music\song1.mp3").Returns(song1);
        _libraryReader.GetSongByFilePathAsync(@"C:\music\missing.mp3").Returns((Song?)null);
        _libraryReader.GetSongByFilePathAsync(@"C:\music\unknown.mp3").Returns((Song?)null);
        _playlistService.CreatePlaylistAsync("playlist1").Returns(playlist1);

        var result = await _service.ImportMultiplePlaylistsAsync(new[] { file1, file2 });

        result.PlaylistsImported.Should().Be(1);
        result.TotalMatchedSongs.Should().Be(1);
        result.TotalUnmatchedSongs.Should().Be(1, "the unmatched path from the partially-successful file should be accumulated");
        result.FailedFiles.Should().ContainSingle(f => f.Contains("playlist2"));
    }

    [Fact]
    public async Task ExportPlaylistAsync_WithMultiArtistSong_ExportsJoinedArtistNames()
    {
        var playlistId = Guid.NewGuid();
        var filePath = Path.Combine(_tempDirectory, "test_playlist.m3u8");
        var playlist = new Playlist { Id = playlistId, Name = "Test Playlist" };

        var song = new Song
        {
            Id = Guid.NewGuid(),
            Title = "Multi-Artist Song",
            Duration = TimeSpan.FromSeconds(180),
            FilePath = "C:\\music\\song.mp3"
        };
        var artist1 = new Artist { Id = Guid.NewGuid(), Name = "Artist A" };
        var artist2 = new Artist { Id = Guid.NewGuid(), Name = "Artist B" };
        song.SongArtists.Add(new SongArtist { Song = song, Artist = artist1, Order = 0 });
        song.SongArtists.Add(new SongArtist { Song = song, Artist = artist2, Order = 1 });
        song.SyncDenormalizedFields();

        _libraryReader.GetPlaylistByIdAsync(playlistId).Returns(playlist);
        _libraryReader.GetSongsInPlaylistOrderedAsync(playlistId).Returns(new List<Song> { song });

        var result = await _service.ExportPlaylistAsync(playlistId, filePath);

        result.Success.Should().BeTrue();
        result.SongCount.Should().Be(1);

        var content = await File.ReadAllTextAsync(filePath);
        content.Should().Contain("#EXTINF:180,Artist A & Artist B - Multi-Artist Song");
        content.Should().Contain("C:\\music\\song.mp3");
    }

    [Fact]
    public async Task ImportPlaylistAsync_WithRepeatedTracks_PreservesFirstOccurrenceAndReportsUniqueSongs()
    {
        var filePath = Path.Combine(_tempDirectory, "playlist.m3u8");
        var first = CreateSong("First", Path.Combine(_tempDirectory, "first.mp3"));
        var second = CreateSong("Second", Path.Combine(_tempDirectory, "second.mp3"));
        var playlist = new Playlist { Id = Guid.NewGuid(), Name = "Repeated" };
        _libraryReader.GetSongByFilePathAsync(first.FilePath).Returns(first);
        _libraryReader.GetSongByFilePathAsync(second.FilePath).Returns(second);
        _playlistService.CreatePlaylistAsync(playlist.Name).Returns(playlist);
        await File.WriteAllLinesAsync(filePath, [first.FilePath, second.FilePath, first.FilePath]);

        var result = await _service.ImportPlaylistAsync(filePath, playlist.Name);

        result.Success.Should().BeTrue();
        result.MatchedSongs.Should().Be(2);
        await _playlistService.Received(1).AddSongsToPlaylistAsync(playlist.Id,
            Arg.Is<IEnumerable<Guid>>(ids => ids.SequenceEqual(new[] { first.Id, second.Id })));
    }

    [Fact]
    public async Task ExportPlaylistAsync_WithMultilineMetadata_KeepsOneLinePerDirective()
    {
        var filePath = Path.Combine(_tempDirectory, "playlist.m3u8");
        var playlist = new Playlist { Id = Guid.NewGuid(), Name = "My\r\nPlaylist" };
        var song = CreateSong("Track\nTitle", @"C:\music\song.mp3");
        song.SongArtists.First().Artist.Name = "Artist\rName";
        song.SyncDenormalizedFields();
        _libraryReader.GetPlaylistByIdAsync(playlist.Id).Returns(playlist);
        _libraryReader.GetSongsInPlaylistOrderedAsync(playlist.Id).Returns(new List<Song> { song });

        var result = await _service.ExportPlaylistAsync(playlist.Id, filePath);

        result.Success.Should().BeTrue();
        (await File.ReadAllLinesAsync(filePath)).Should().Equal(
            "#EXTM3U", "#PLAYLIST:My Playlist", "#EXTINF:180,Artist Name - Track Title", song.FilePath);
    }

    private static Song CreateSong(string title, string filePath)
    {
        var song = new Song
        {
            Title = title,
            FilePath = filePath,
            DurationTicks = TimeSpan.FromMinutes(3).Ticks
        };
        song.SongArtists.Add(new SongArtist
        {
            Song = song,
            Artist = new Artist { Name = "Artist" },
            Order = 0
        });
        song.SyncDenormalizedFields();
        return song;
    }
    [Theory]
    [InlineData("invalid\0path.mp3")]
    [InlineData("https://example.com/stream.mp3")]
    [InlineData("file://")]
    public async Task ImportPlaylistAsync_WithInvalidEntry_ImportsRemainingSongs(string invalidEntry)
    {
        var filePath = Path.Combine(_tempDirectory, "playlist.m3u8");
        var song = new Song { Id = Guid.NewGuid(), FilePath = Path.Combine(_tempDirectory, "valid.mp3") };
        var playlist = new Playlist { Id = Guid.NewGuid(), Name = "Mixed" };
        _libraryReader.GetSongByFilePathAsync(song.FilePath).Returns(song);
        _playlistService.CreatePlaylistAsync(playlist.Name).Returns(playlist);
        await File.WriteAllLinesAsync(filePath, [invalidEntry, song.FilePath]);

        var result = await _service.ImportPlaylistAsync(filePath, playlist.Name);

        result.Success.Should().BeTrue();
        result.MatchedSongs.Should().Be(1);
        result.UnmatchedPaths.Should().Equal(invalidEntry);
        await _libraryReader.Received(1).GetSongByFilePathAsync(Arg.Any<string>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImportPlaylistAsync_WithSpecialCharacters_ResolvesLiteralPathsAndFileUris(bool useFileUri)
    {
        var filePath = Path.Combine(_tempDirectory, "playlist.m3u8");
        var song = new Song { Id = Guid.NewGuid(), FilePath = Path.Combine(_tempDirectory, "Music #1", "café 100%.mp3") };
        var playlist = new Playlist { Id = Guid.NewGuid(), Name = "URI" };
        _libraryReader.GetSongByFilePathAsync(song.FilePath).Returns(song);
        _playlistService.CreatePlaylistAsync(playlist.Name).Returns(playlist);
        await File.WriteAllTextAsync(filePath, useFileUri ? new Uri(song.FilePath).AbsoluteUri : song.FilePath);

        var result = await _service.ImportPlaylistAsync(filePath, playlist.Name);

        result.Success.Should().BeTrue();
        result.MatchedSongs.Should().Be(1);
        result.UnmatchedPaths.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportPlaylistAsync_WhenAddingSongsFails_RemovesNewPlaylist(bool throws)
    {
        var filePath = Path.Combine(_tempDirectory, "playlist.m3u8");
        var song = new Song { Id = Guid.NewGuid(), FilePath = Path.Combine(_tempDirectory, "valid.mp3") };
        var playlist = new Playlist { Id = Guid.NewGuid(), Name = "Failed" };
        _libraryReader.GetSongByFilePathAsync(song.FilePath).Returns(song);
        _playlistService.CreatePlaylistAsync(playlist.Name).Returns(playlist);
        _playlistService.AddSongsToPlaylistAsync(playlist.Id, Arg.Any<IEnumerable<Guid>>())
            .Returns(throws ? Task.FromException<bool>(new IOException("Write failed")) : Task.FromResult(false));
        await File.WriteAllTextAsync(filePath, song.FilePath);

        var result = await _service.ImportPlaylistAsync(filePath, playlist.Name);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        await _playlistService.Received(1).DeletePlaylistAsync(playlist.Id);
    }
}

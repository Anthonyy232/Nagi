using FluentAssertions;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Implementations;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

public class M3uPlaylistExportServiceTests
{
    private readonly ILibraryReader _libraryReader;
    private readonly IPlaylistService _playlistService;
    private readonly ILogger<M3uPlaylistExportService> _logger;
    private readonly M3uPlaylistExportService _service;

    public M3uPlaylistExportServiceTests()
    {
        _libraryReader = Substitute.For<ILibraryReader>();
        _playlistService = Substitute.For<IPlaylistService>();
        _logger = Substitute.For<ILogger<M3uPlaylistExportService>>();
        _service = new M3uPlaylistExportService(_libraryReader, _playlistService, _logger);
    }

    // -------------------------------------------------------------------------
    // ExportPlaylistAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ExportPlaylistAsync_WhenPlaylistNotFound_ReturnsFailureResult()
    {
        var playlistId = Guid.NewGuid();
        _libraryReader.GetPlaylistByIdAsync(playlistId).Returns((Playlist?)null);

        var result = await _service.ExportPlaylistAsync(playlistId, Path.GetTempFileName());

        result.Success.Should().BeFalse();
        result.SongCount.Should().Be(0);
    }

    [Fact]
    public async Task ExportPlaylistAsync_WritesExtm3uAndPlaylistHeaders()
    {
        var playlistId = Guid.NewGuid();
        var filePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.m3u8");
        var playlist = new Playlist { Id = playlistId, Name = "My Playlist" };
        _libraryReader.GetPlaylistByIdAsync(playlistId).Returns(playlist);
        _libraryReader.GetSongsInPlaylistOrderedAsync(playlistId).Returns(new List<Song>());

        try
        {
            await _service.ExportPlaylistAsync(playlistId, filePath);

            var content = await File.ReadAllTextAsync(filePath);
            content.Should().StartWith("#EXTM3U", "M3U files must begin with the #EXTM3U header");
            content.Should().Contain("#PLAYLIST:My Playlist", "the playlist name must appear in the #PLAYLIST directive");
        }
        finally
        {
            if (File.Exists(filePath)) File.Delete(filePath);
        }
    }

    [Fact]
    public async Task ExportPlaylistAsync_WritesExtinfLineWithDurationAndArtistTitle()
    {
        var playlistId = Guid.NewGuid();
        var filePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.m3u8");
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

        try
        {
            var result = await _service.ExportPlaylistAsync(playlistId, filePath);

            result.Success.Should().BeTrue();
            var content = await File.ReadAllTextAsync(filePath);
            content.Should().Contain("#EXTINF:240,The Artist - Track One");
            content.Should().Contain(@"C:\music\track.mp3");
        }
        finally
        {
            if (File.Exists(filePath)) File.Delete(filePath);
        }
    }

    // -------------------------------------------------------------------------
    // ImportPlaylistAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ImportPlaylistAsync_WhenFileDoesNotExist_ReturnsFailureResult()
    {
        var result = await _service.ImportPlaylistAsync(@"C:\nonexistent\file.m3u", "My Playlist");

        result.Success.Should().BeFalse();
        result.PlaylistId.Should().BeNull();
    }

    [Fact]
    public async Task ImportPlaylistAsync_WhenNoSongsMatchLibrary_ReturnsFailureWithUnmatchedPaths()
    {
        var m3uPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.m3u");
        await File.WriteAllTextAsync(m3uPath, $"#EXTM3U\nC:\\unknown\\song.mp3");
        _libraryReader.GetSongByFilePathAsync(Arg.Any<string>()).Returns((Song?)null);

        try
        {
            var result = await _service.ImportPlaylistAsync(m3uPath, "My Playlist");

            result.Success.Should().BeFalse("import should fail when nothing in the file matches the library");
            result.UnmatchedSongs.Should().Be(1);
            result.UnmatchedPaths.Should().Contain(@"C:\unknown\song.mp3");
        }
        finally
        {
            if (File.Exists(m3uPath)) File.Delete(m3uPath);
        }
    }

    [Fact]
    public async Task ImportPlaylistAsync_WithPartialLibraryMatch_ReturnsSuccessWithCorrectCounts()
    {
        var knownSong = new Song { Id = Guid.NewGuid(), FilePath = @"C:\music\known.mp3" };
        var newPlaylist = new Playlist { Id = Guid.NewGuid(), Name = "Test" };
        var m3uPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.m3u");
        await File.WriteAllLinesAsync(m3uPath, new[]
        {
            "#EXTM3U",
            @"C:\music\known.mp3",
            @"C:\music\missing.mp3"
        });
        _libraryReader.GetSongByFilePathAsync(@"C:\music\known.mp3").Returns(knownSong);
        _libraryReader.GetSongByFilePathAsync(@"C:\music\missing.mp3").Returns((Song?)null);
        _playlistService.CreatePlaylistAsync("Test").Returns(newPlaylist);

        try
        {
            var result = await _service.ImportPlaylistAsync(m3uPath, "Test");

            result.Success.Should().BeTrue();
            result.MatchedSongs.Should().Be(1);
            result.UnmatchedSongs.Should().Be(1);
            result.UnmatchedPaths.Should().Contain(@"C:\music\missing.mp3");
            await _playlistService.Received(1).AddSongsToPlaylistAsync(newPlaylist.Id, Arg.Is<IEnumerable<Guid>>(ids => ids.Contains(knownSong.Id)));
        }
        finally
        {
            if (File.Exists(m3uPath)) File.Delete(m3uPath);
        }
    }

    [Fact]
    public async Task ImportPlaylistAsync_SkipsM3uDirectivesAndBlankLines()
    {
        // Lines starting with '#' and empty lines are M3U metadata — they must never
        // be passed to the library lookup as file paths.
        var matchedSong = new Song { Id = Guid.NewGuid(), FilePath = @"C:\music\track.mp3" };
        var newPlaylist = new Playlist { Id = Guid.NewGuid(), Name = "P" };
        var m3uPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.m3u");
        await File.WriteAllTextAsync(m3uPath,
            "#EXTM3U\n" +
            "#PLAYLIST:My Playlist\n" +
            "\n" +
            "#EXTINF:180,Artist - Track\n" +
            @"C:\music\track.mp3" + "\n" +
            "# A comment\n");
        _libraryReader.GetSongByFilePathAsync(@"C:\music\track.mp3").Returns(matchedSong);
        _playlistService.CreatePlaylistAsync(Arg.Any<string>()).Returns(newPlaylist);

        try
        {
            var result = await _service.ImportPlaylistAsync(m3uPath, "P");

            result.Success.Should().BeTrue();
            result.MatchedSongs.Should().Be(1, "only the one real file path should be matched");
            result.UnmatchedSongs.Should().Be(0, "comment and directive lines must not be treated as file paths");
        }
        finally
        {
            if (File.Exists(m3uPath)) File.Delete(m3uPath);
        }
    }

    [Fact]
    public async Task ImportPlaylistAsync_ResolvesRelativePathsAgainstM3uDirectory()
    {
        // If the M3U file references songs with relative paths, they must be resolved
        // to absolute paths before looking them up in the library.
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var m3uPath = Path.Combine(tempDir, "playlist.m3u");
        var relativeSongPath = "songs/track.mp3";
        var expectedAbsPath = Path.GetFullPath(Path.Combine(tempDir, relativeSongPath));

        await File.WriteAllTextAsync(m3uPath, $"#EXTM3U\n{relativeSongPath}");
        var matchedSong = new Song { Id = Guid.NewGuid(), FilePath = expectedAbsPath };
        var newPlaylist = new Playlist { Id = Guid.NewGuid(), Name = "playlist" };
        _libraryReader.GetSongByFilePathAsync(expectedAbsPath).Returns(matchedSong);
        _playlistService.CreatePlaylistAsync(Arg.Any<string>()).Returns(newPlaylist);

        try
        {
            var result = await _service.ImportPlaylistAsync(m3uPath, "playlist");

            result.Success.Should().BeTrue("relative path should be resolved to an absolute path for the library lookup");
            result.MatchedSongs.Should().Be(1);
        }
        finally
        {
            if (File.Exists(m3uPath)) File.Delete(m3uPath);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // ExportAllPlaylistsAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ExportAllPlaylistsAsync_WhenNoPlaylistsExist_ReturnsFailure()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _libraryReader.GetAllPlaylistsAsync().Returns(Enumerable.Empty<Playlist>());

        try
        {
            var result = await _service.ExportAllPlaylistsAsync(tempDir);

            result.Success.Should().BeFalse();
            result.PlaylistsExported.Should().Be(0);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExportAllPlaylistsAsync_EmptyPlaylistsAreNotExported()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var emptyPlaylist = new Playlist { Id = Guid.NewGuid(), Name = "Empty" };
        var filledPlaylist = new Playlist { Id = Guid.NewGuid(), Name = "Filled" };
        var song = new Song { Title = "S", FilePath = @"C:\music\s.mp3", DurationTicks = TimeSpan.FromMinutes(3).Ticks };
        song.SongArtists.Add(new SongArtist { Song = song, Artist = new Artist { Name = "A" }, Order = 0 });
        song.SyncDenormalizedFields();

        _libraryReader.GetAllPlaylistsAsync().Returns(new[] { emptyPlaylist, filledPlaylist });
        _libraryReader.GetSongsInPlaylistOrderedAsync(emptyPlaylist.Id).Returns(Enumerable.Empty<Song>());
        _libraryReader.GetSongsInPlaylistOrderedAsync(filledPlaylist.Id).Returns(new List<Song> { song });
        // ExportPlaylistAsync is called internally and will call GetPlaylistByIdAsync
        _libraryReader.GetPlaylistByIdAsync(filledPlaylist.Id).Returns(filledPlaylist);

        try
        {
            var result = await _service.ExportAllPlaylistsAsync(tempDir);

            result.Success.Should().BeTrue();
            result.PlaylistsExported.Should().Be(1, "the empty playlist should be skipped");
            result.TotalSongs.Should().Be(1);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // ImportMultiplePlaylistsAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ImportMultiplePlaylistsAsync_AggregatesResultsAcrossAllFiles()
    {
        // file1: 2 paths — one matches, one doesn't → partial success, unmatched accumulates
        // file2: 1 path  — no match at all → counted as a failed file
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
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

        try
        {
            var result = await _service.ImportMultiplePlaylistsAsync(new[] { file1, file2 });

            // file1 partially succeeded (1 matched, 1 unmatched)
            result.PlaylistsImported.Should().Be(1);
            result.TotalMatchedSongs.Should().Be(1);
            result.TotalUnmatchedSongs.Should().Be(1, "the unmatched path from the partially-successful file should be accumulated");
            // file2 had zero matches, so it is counted as a failed file
            result.FailedFiles.Should().ContainSingle(f => f.Contains("playlist2"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExportPlaylistAsync_WithMultiArtistSong_ExportsJoinedArtistNames()
    {
        // Arrange
        var playlistId = Guid.NewGuid();
        var filePath = Path.Combine(Path.GetTempPath(), "test_playlist.m3u8");
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

        try
        {
            // Act
            var result = await _service.ExportPlaylistAsync(playlistId, filePath);

            // Assert
            result.Success.Should().BeTrue();
            result.SongCount.Should().Be(1);

            var content = await File.ReadAllTextAsync(filePath);
            content.Should().Contain("#EXTINF:180,Artist A & Artist B - Multi-Artist Song");
            content.Should().Contain("C:\\music\\song.mp3");
        }
        finally
        {
            if (File.Exists(filePath)) File.Delete(filePath);
        }
    }
}

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

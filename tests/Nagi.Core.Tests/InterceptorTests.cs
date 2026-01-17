using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nagi.Core.Models;
using Nagi.Core.Tests.Utils;
using Xunit;

namespace Nagi.Core.Tests;

public class InterceptorTests : IDisposable
{
    private readonly DbContextFactoryTestHelper _dbHelper;

    public InterceptorTests()
    {
        _dbHelper = new DbContextFactoryTestHelper();
    }

    public void Dispose()
    {
        _dbHelper.Dispose();
    }

    [Fact]
    public async Task SaveChanges_WithNewSongAndArtists_PopulatesDenormalizedFields()
    {
        // Arrange
        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        var folder = new Folder { Path = "C:\\Music", Name = "Music" };
        context.Folders.Add(folder);

        var artist1 = new Artist { Name = "Artist A" };
        var artist2 = new Artist { Name = "Artist B" };
        var song = new Song 
        { 
            Title = "New Song", 
            Folder = folder,
            FilePath = "C:\\Music\\song.mp3",
            DirectoryPath = "C:\\Music"
        };
        
        song.SongArtists.Add(new SongArtist { Artist = artist1, Order = 0 });
        song.SongArtists.Add(new SongArtist { Artist = artist2, Order = 1 });
        
        context.Artists.AddRange(artist1, artist2);
        context.Songs.Add(song);

        // Act
        await context.SaveChangesAsync();

        // Assert
        song.ArtistName.Should().Be("Artist A & Artist B");
        song.PrimaryArtistName.Should().Be("Artist A");
    }

    [Fact]
    public async Task SaveChanges_WhenAddingArtistToExistingSong_UpdatesDenormalizedFields()
    {
        // Arrange: Create a song with one artist first
        Guid songId;
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var folder = new Folder { Path = "C:\\Music", Name = "Music" };
            context.Folders.Add(folder);

            var artist1 = new Artist { Name = "Artist 1" };
            var song = new Song 
            { 
                Title = "Existing Song",
                Folder = folder,
                FilePath = "C:\\Music\\existing.mp3",
                DirectoryPath = "C:\\Music"
            };
            song.SongArtists.Add(new SongArtist { Artist = artist1, Order = 0 });
            context.Artists.Add(artist1);
            context.Songs.Add(song);
            await context.SaveChangesAsync();
            songId = song.Id;
        }

        // Act: Load the song and add a second artist (relationship-only change)
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var song = await context.Songs
                .Include(s => s.SongArtists)
                .FirstAsync(s => s.Id == songId);
            
            var artist2 = new Artist { Name = "Artist 2" };
            context.Artists.Add(artist2);
            
            // Add relationship - this should not mark 'song' as Modified yet, 
            // but the Interceptor should find it anyway via the SongArtist entry.
            song.SongArtists.Add(new SongArtist { Artist = artist2, Order = 1 });
            
            // Verify our assumption: the song itself is still 'Unchanged' in the change tracker
            context.Entry(song).State.Should().Be(EntityState.Unchanged);
            
            await context.SaveChangesAsync();
        }

        // Assert: Verify the string was updated in the DB
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var song = await context.Songs.FirstAsync(s => s.Id == songId);
            song.ArtistName.Should().Be("Artist 1 & Artist 2");
        }
    }

    [Fact]
    public async Task SaveChanges_WhenRemovingArtistFromSong_UpdatesDenormalizedFields()
    {
        // Arrange
        Guid songId;
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var folder = new Folder { Path = "C:\\Music", Name = "Music" };
            context.Folders.Add(folder);

            var artist1 = new Artist { Name = "A" };
            var artist2 = new Artist { Name = "B" };
            var song = new Song 
            { 
                Title = "Multi",
                Folder = folder,
                FilePath = "C:\\Music\\multi.mp3",
                DirectoryPath = "C:\\Music"
            };
            song.SongArtists.Add(new SongArtist { Artist = artist1, Order = 0 });
            song.SongArtists.Add(new SongArtist { Artist = artist2, Order = 1 });
            context.Artists.AddRange(artist1, artist2);
            context.Songs.Add(song);
            await context.SaveChangesAsync();
            songId = song.Id;
        }

        // Act
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var song = await context.Songs
                .Include(s => s.SongArtists)
                .FirstAsync(s => s.Id == songId);
            
            var saToRemove = song.SongArtists.First(sa => sa.Order == 1);
            song.SongArtists.Remove(saToRemove);
            
            await context.SaveChangesAsync();
        }

        // Assert
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var song = await context.Songs.FirstAsync(s => s.Id == songId);
            song.ArtistName.Should().Be("A");
        }
    }
}

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

    /// <summary>
    ///     Verifies that when a song is attached without its SongArtists navigation property loaded,
    ///     marking it as Modified and saving does NOT wipe the ArtistName field when defensive 
    ///     loading is applied (as done in UpdateSongAsync).
    /// </summary>
    [Fact]
    public async Task SaveChanges_WhenSongAttachedWithDefensiveLoading_PreservesArtistName()
    {
        // Arrange: Create a song with artists
        Guid songId;
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var folder = new Folder { Path = "C:\\Music", Name = "Music" };
            context.Folders.Add(folder);

            var artist = new Artist { Name = "Test Artist" };
            var song = new Song 
            { 
                Title = "Test Song",
                Folder = folder,
                FilePath = "C:\\Music\\test.mp3",
                DirectoryPath = "C:\\Music"
            };
            song.SongArtists.Add(new SongArtist { Artist = artist, Order = 0 });
            context.Artists.Add(artist);
            context.Songs.Add(song);
            await context.SaveChangesAsync();
            songId = song.Id;
        }

        // Act: Load song WITHOUT SongArtists (simulates AsNoTracking query), then attach and update
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            // Load without navigation (SongArtists is empty collection by default)
            var song = await context.Songs
                .AsNoTracking()
                .FirstAsync(s => s.Id == songId);
            
            // Verify the song loaded without artists
            song.SongArtists.Should().BeEmpty();
            
            // Attach and apply defensive loading (as UpdateSongAsync does)
            context.Songs.Attach(song);
            await context.Entry(song)
                .Collection(s => s.SongArtists)
                .Query()
                .Include(sa => sa.Artist)
                .LoadAsync();
            
            // Now modify the song
            song.Title = "Updated Title";
            context.Entry(song).State = EntityState.Modified;
            
            await context.SaveChangesAsync();
        }

        // Assert: ArtistName should be preserved
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var song = await context.Songs.FirstAsync(s => s.Id == songId);
            song.Title.Should().Be("Updated Title");
            song.ArtistName.Should().Be("Test Artist");
            song.PrimaryArtistName.Should().Be("Test Artist");
        }
    }

    /// <summary>
    ///     Verifies that when a song is simply modified (without SongArtist changes),
    ///     the interceptor does NOT attempt to sync denormalized fields at all.
    ///     This is the expected behavior - the interceptor only syncs when:
    ///     1. The song is newly Added
    ///     2. SongArtist relationships are Added, Modified, or Deleted
    /// </summary>
    [Fact]
    public async Task SaveChanges_WhenSongModifiedWithoutSongArtistChanges_DoesNotTriggerSync()
    {
        // Arrange: Create a song with artists
        Guid songId;
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var folder = new Folder { Path = "C:\\Music", Name = "Music" };
            context.Folders.Add(folder);

            var artist = new Artist { Name = "Original Artist" };
            var song = new Song 
            { 
                Title = "Original Song",
                Folder = folder,
                FilePath = "C:\\Music\\original.mp3",
                DirectoryPath = "C:\\Music"
            };
            song.SongArtists.Add(new SongArtist { Artist = artist, Order = 0 });
            context.Artists.Add(artist);
            context.Songs.Add(song);
            await context.SaveChangesAsync();
            songId = song.Id;
            
            // Verify setup
            song.ArtistName.Should().Be("Original Artist");
        }

        // Act: Load song, modify only non-relationship fields, and save
        // The interceptor should NOT be triggered since no SongArtist changes
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var song = await context.Songs.FirstAsync(s => s.Id == songId);
            song.Title = "Modified Title";
            
            await context.SaveChangesAsync();
        }

        // Assert: ArtistName is preserved because interceptor didn't sync
        // (no SongArtist relationship changes occurred)
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var song = await context.Songs.FirstAsync(s => s.Id == songId);
            song.Title.Should().Be("Modified Title");
            song.ArtistName.Should().Be("Original Artist");
        }
    }


    /// <summary>
    ///     Verifies that Unicode combining characters in artist names are handled correctly.
    /// </summary>
    [Fact]
    public async Task SaveChanges_WithUnicodeCombiningCharacters_PreservesArtistName()
    {
        // Arrange: Create a song with an artist using combining characters
        // "José" represented as "Jose\u0301" (J-o-s-e + combining acute accent)
        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        var folder = new Folder { Path = "C:\\Music", Name = "Music" };
        context.Folders.Add(folder);

        var artistWithCombining = new Artist { Name = "Jose\u0301" }; // Combining form
        var song = new Song 
        { 
            Title = "Spanish Song",
            Folder = folder,
            FilePath = "C:\\Music\\spanish.mp3",
            DirectoryPath = "C:\\Music"
        };
        song.SongArtists.Add(new SongArtist { Artist = artistWithCombining, Order = 0 });
        context.Artists.Add(artistWithCombining);
        context.Songs.Add(song);

        // Act
        await context.SaveChangesAsync();

        // Assert: The combining character representation should be preserved
        song.ArtistName.Should().Be("Jose\u0301");
        song.PrimaryArtistName.Should().Be("Jose\u0301");
    }

    /// <summary>
    ///     Verifies that right-to-left text in artist names is handled correctly.
    /// </summary>
    [Fact]
    public async Task SaveChanges_WithRtlArtistName_PreservesArtistName()
    {
        // Arrange
        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        var folder = new Folder { Path = "C:\\Music", Name = "Music" };
        context.Folders.Add(folder);

        // Arabic artist name
        var arabicArtist = new Artist { Name = "فنان عربي" };
        var song = new Song 
        { 
            Title = "Arabic Song",
            Folder = folder,
            FilePath = "C:\\Music\\arabic.mp3",
            DirectoryPath = "C:\\Music"
        };
        song.SongArtists.Add(new SongArtist { Artist = arabicArtist, Order = 0 });
        context.Artists.Add(arabicArtist);
        context.Songs.Add(song);

        // Act
        await context.SaveChangesAsync();

        // Assert
        song.ArtistName.Should().Be("فنان عربي");
        song.PrimaryArtistName.Should().Be("فنان عربي");
    }

    /// <summary>
    ///     Verifies that mixed LTR and RTL text in multiple artists is handled correctly.
    /// </summary>
    [Fact]
    public async Task SaveChanges_WithMixedLtrAndRtlArtists_FormatsCorrectly()
    {
        // Arrange
        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        var folder = new Folder { Path = "C:\\Music", Name = "Music" };
        context.Folders.Add(folder);

        var englishArtist = new Artist { Name = "English Artist" };
        var hebrewArtist = new Artist { Name = "אמן עברי" }; // Hebrew artist
        var song = new Song 
        { 
            Title = "Collaboration",
            Folder = folder,
            FilePath = "C:\\Music\\collab.mp3",
            DirectoryPath = "C:\\Music"
        };
        song.SongArtists.Add(new SongArtist { Artist = englishArtist, Order = 0 });
        song.SongArtists.Add(new SongArtist { Artist = hebrewArtist, Order = 1 });
        context.Artists.AddRange(englishArtist, hebrewArtist);
        context.Songs.Add(song);

        // Act
        await context.SaveChangesAsync();

        // Assert: Both artists should be present with proper separator
        song.ArtistName.Should().Be("English Artist & אמן עברי");
        song.PrimaryArtistName.Should().Be("English Artist");
    }
}


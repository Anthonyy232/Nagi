using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nagi.Core.Models;
using Nagi.Core.Tests.Utils;
using Xunit;

namespace Nagi.Core.Tests;

public class InterceptorTests : IDisposable
{
    private readonly DbContextFactoryTestHelper _dbHelper = new();

    public void Dispose()
    {
        _dbHelper.Dispose();
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task SaveChanges_WhenTitlesChange_UpdatesSortKeysWithoutLoadingArtists(
        bool autoDetectChanges, bool saveAsync)
    {
        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        var artist = new Artist { Name = "The Artist" };
        var album = new Album { Title = "The Original Album" };
        album.AlbumArtists.Add(new AlbumArtist { Artist = artist });
        var song = new Song
        {
            Title = "The Original Song",
            FilePath = "C:\\Music\\original.mp3",
            DirectoryPath = "C:\\Music",
            Folder = new Folder { Path = "C:\\Music", Name = "Music" },
            Album = album
        };
        song.SongArtists.Add(new SongArtist { Artist = artist });
        context.Songs.Add(song);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        song = await context.Songs.SingleAsync();
        album = await context.Albums.SingleAsync();
        context.ChangeTracker.AutoDetectChangesEnabled = autoDetectChanges;
        context.Entry(song).Property(s => s.Title).CurrentValue = "A New Song";
        context.Entry(album).Property(a => a.Title).CurrentValue = "An Updated Album";
        if (saveAsync)
            await context.SaveChangesAsync();
        else
            context.SaveChanges();

        song.SongArtists.Should().BeEmpty();
        album.AlbumArtists.Should().BeEmpty();
        context.ChangeTracker.Clear();
        song = await context.Songs.SingleAsync();
        album = await context.Albums.SingleAsync();
        song.SortTitle.Should().Be("new song");
        album.SortTitle.Should().Be("updated album");
        song.ArtistName.Should().Be("The Artist");
        album.ArtistName.Should().Be("The Artist");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveChanges_WithAutomaticDetectionDisabled_PersistsArtistFieldsOnModifiedEntities(bool saveAsync)
    {
        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        var artist = new Artist { Name = "First Artist" };
        var album = new Album { Title = "Album" };
        album.AlbumArtists.Add(new AlbumArtist { Artist = artist });
        var song = new Song
        {
            Title = "Song",
            FilePath = "C:\\Music\\song.mp3",
            DirectoryPath = "C:\\Music",
            Folder = new Folder { Path = "C:\\Music", Name = "Music" },
            Album = album
        };
        song.SongArtists.Add(new SongArtist { Artist = artist });
        context.Songs.Add(song);
        await context.SaveChangesAsync();
        context.ChangeTracker.AutoDetectChangesEnabled = false;

        var secondArtist = new Artist { Name = "Second Artist" };
        context.SongArtists.Add(new SongArtist { Song = song, Artist = secondArtist, Order = 1 });
        context.AlbumArtists.Add(new AlbumArtist { Album = album, Artist = secondArtist, Order = 1 });
        context.Entry(song).Property(s => s.Year).CurrentValue = 2026;
        context.Entry(album).Property(a => a.Year).CurrentValue = 2026;
        if (saveAsync)
            await context.SaveChangesAsync();
        else
            context.SaveChanges();

        context.ChangeTracker.Clear();
        song = await context.Songs.SingleAsync();
        album = await context.Albums.SingleAsync();
        song.ArtistName.Should().Be("First Artist & Second Artist");
        album.ArtistName.Should().Be("First Artist & Second Artist");
        song.Year.Should().Be(2026);
        album.Year.Should().Be(2026);
    }

    [Theory]
    [InlineData("Artist A", "Artist B", "Artist A & Artist B")]
    [InlineData("Jose\u0301", "Artist B", "Jose\u0301 & Artist B")]
    [InlineData("فنان عربي", "Artist B", "فنان عربي & Artist B")]
    [InlineData("English Artist", "אמן עברי", "English Artist & אמן עברי")]
    public async Task SaveChanges_WithNewSongAndArtists_PopulatesDenormalizedFields(
        string firstArtist, string secondArtist, string displayName)
    {
        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        var folder = new Folder { Path = "C:\\Music", Name = "Music" };
        context.Folders.Add(folder);

        var artist1 = new Artist { Name = firstArtist };
        var artist2 = new Artist { Name = secondArtist };
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
        await context.SaveChangesAsync();
        song.ArtistName.Should().Be(displayName);
        song.PrimaryArtistName.Should().Be(firstArtist);
    }

    [Fact]
    public async Task SaveChanges_WhenAddingArtistToExistingSong_UpdatesDenormalizedFields()
    {
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
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var song = await context.Songs
                .Include(s => s.SongArtists)
                .FirstAsync(s => s.Id == songId);

            var artist2 = new Artist { Name = "Artist 2" };
            context.Artists.Add(artist2);
            song.SongArtists.Add(new SongArtist { Artist = artist2, Order = 1 });
            context.Entry(song).State.Should().Be(EntityState.Unchanged);

            await context.SaveChangesAsync();
        }
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var song = await context.Songs.FirstAsync(s => s.Id == songId);
            song.ArtistName.Should().Be("Artist 1 & Artist 2");
        }
    }

    [Fact]
    public async Task SaveChanges_WhenRemovingArtistFromSong_UpdatesDenormalizedFields()
    {
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
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var song = await context.Songs
                .Include(s => s.SongArtists)
                .FirstAsync(s => s.Id == songId);

            var saToRemove = song.SongArtists.First(sa => sa.Order == 1);
            song.SongArtists.Remove(saToRemove);

            await context.SaveChangesAsync();
        }
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var song = await context.Songs.FirstAsync(s => s.Id == songId);
            song.ArtistName.Should().Be("A");
        }
    }

}

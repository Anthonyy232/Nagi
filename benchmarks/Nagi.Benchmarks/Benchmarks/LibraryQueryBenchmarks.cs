using BenchmarkDotNet.Attributes;
using Microsoft.EntityFrameworkCore;
using Nagi.Benchmarks.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Benchmarks.Benchmarks;

[MemoryDiagnoser]
public class LibraryQueryBenchmarks
{
    private const int PageSize = 100;
    private const string GuestArtistName = "Guest Artist Needle";
    private LibraryBenchmarkFixture _fixture = null!;
    private Guid _smartPlaylistId;

    [Params(10_000, 100_000)]
    public int SongCount;

    [GlobalSetup]
    public async Task Setup()
    {
        _fixture = new LibraryBenchmarkFixture();
        await SeedLibraryAsync();
        VerifyPage(await BrowseFirstPage(), SongCount);
        VerifyPage(await BrowseLastPage(), SongCount);
        VerifyPage(await SearchGuestArtist(), SongCount / 100);
        VerifyPage(await SearchNoMatches(), 0);
        VerifyPage(await SmartPlaylistPage(), SongCount / 20);
    }

    [GlobalCleanup]
    public void Cleanup() => _fixture.Dispose();

    [Benchmark]
    public Task<PagedResult<Song>> BrowseFirstPage() =>
        _fixture.Reader.GetAllSongsPagedAsync(1, PageSize);

    [Benchmark]
    public Task<PagedResult<Song>> BrowseLastPage() =>
        _fixture.Reader.GetAllSongsPagedAsync(SongCount / PageSize, PageSize);

    [Benchmark]
    public Task<PagedResult<Song>> SearchGuestArtist() =>
        _fixture.Reader.SearchSongsPagedAsync(GuestArtistName, 1, PageSize);

    [Benchmark]
    public Task<PagedResult<Song>> SearchNoMatches() =>
        _fixture.Reader.SearchSongsPagedAsync("Missing Artist Needle", 1, PageSize);

    [Benchmark]
    public Task<PagedResult<Song>> SmartPlaylistPage() =>
        _fixture.SmartPlaylists.GetMatchingSongsPagedAsync(_smartPlaylistId, 1, PageSize);

    private static void VerifyPage(PagedResult<Song> page, int expectedCount)
    {
        if (page.TotalCount != expectedCount || page.Items.Count() != Math.Min(PageSize, expectedCount))
            throw new InvalidOperationException($"Expected {expectedCount} matches, got {page.TotalCount} with {page.Items.Count()} rows.");
    }

    private async Task SeedLibraryAsync()
    {
        var folder = new Folder { Name = "Benchmark Library", Path = _fixture.RootPath };
        var artists = Enumerable.Range(0, SongCount / 50)
            .Select(index => new Artist { Name = $"Artist {index:D4}" }).ToArray();
        var guest = new Artist { Name = GuestArtistName };
        var genres = new[] { new Genre { Name = "Rock" }, new Genre { Name = "Jazz" } };
        var playlist = new SmartPlaylist
        {
            Name = "Played favorites",
            MatchAllRules = true,
            Rules =
            [
                new() { Field = SmartPlaylistField.IsLoved, Operator = SmartPlaylistOperator.IsTrue },
                new() { Field = SmartPlaylistField.PlayCount, Operator = SmartPlaylistOperator.GreaterThan, Value = "5", Order = 1 }
            ]
        };
        _smartPlaylistId = playlist.Id;

        await using (var context = await _fixture.ContextFactory.CreateDbContextAsync())
        {
            context.AddRange(folder, guest, playlist);
            context.Artists.AddRange(artists);
            context.Genres.AddRange(genres);
            await context.SaveChangesAsync();
        }

        // Seed through the real model/interceptor, outside the timed queries. Batch tracking
        // keeps fixture construction bounded; lyrics exercise the list-row projection.
        var lyrics = new string('l', 4096);
        const int batchSize = 1000;
        for (var start = 0; start < SongCount; start += batchSize)
        {
            await using var context = await _fixture.ContextFactory.CreateDbContextAsync();
            // Fresh entities prevent relationship fixup from retaining earlier batches.
            var batchArtists = artists.Skip(start / 50).Take(batchSize / 50)
                .Select(artist => new Artist { Id = artist.Id, Name = artist.Name }).ToArray();
            var batchGuest = new Artist { Id = guest.Id, Name = guest.Name };
            var batchGenres = genres.Select(genre => new Genre { Id = genre.Id, Name = genre.Name }).ToArray();
            context.AttachRange(batchArtists);
            context.Attach(batchGuest);
            context.AttachRange(batchGenres);
            var albums = Enumerable.Range(start / 10, batchSize / 10).Select(index => new Album
            {
                Title = $"Album {index:D5}",
                AlbumArtists = [new() { Artist = batchArtists[(index - start / 10) / 5], Order = 0 }]
            }).ToArray();
            context.Albums.AddRange(albums);

            for (var index = start; index < start + batchSize; index++)
            {
                var song = new Song
                {
                    Title = $"Track {index:D6}",
                    FolderId = folder.Id,
                    Album = albums[(index - start) / 10],
                    FilePath = Path.Combine(_fixture.RootPath, $"Track {index:D6}.mp3"),
                    DirectoryPath = _fixture.RootPath,
                    Duration = TimeSpan.FromMinutes(3),
                    Year = 1980 + index % 40,
                    TrackNumber = index % 10 + 1,
                    PlayCount = index % 20,
                    IsLoved = index % 10 == 0,
                    Lyrics = index % 5 == 0 ? lyrics : null,
                    Genres = [batchGenres[index % 2]],
                    SongArtists = [new() { Artist = batchArtists[(index - start) / 50], Order = 0 }]
                };
                if (index % 100 == 0)
                    song.SongArtists.Add(new SongArtist { Artist = batchGuest, Order = 1 });
                context.Songs.Add(song);
            }
            await context.SaveChangesAsync();
        }
    }
}

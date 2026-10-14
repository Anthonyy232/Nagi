using FluentAssertions;
using Nagi.Core.Models;
using Nagi.Core.Services.Data;
using Xunit;

namespace Nagi.Core.Tests;

public partial class LibraryServiceTests
{
    [Fact]
    public async Task Favorites_SearchPagingAndPlaybackIdsUseTheSameFilteredCollection()
    {
        var folder = new Folder { Name = "Music", Path = "C:\\Music" };
        Song[] songs = [
            new() { Folder = folder, Title = "Keep 100%", FilePath = "C:\\Music\\1.mp3" },
            new() { Folder = folder, Title = "Keep 100% again", FilePath = "C:\\Music\\2.mp3" },
            new() { Folder = folder, Title = "Other", FilePath = "C:\\Music\\3.mp3" }
        ];
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Songs.AddRange(songs);
            await context.SaveChangesAsync();
        }
        await _libraryService.SetSongsLovedAsync(songs.Select(s => s.Id).Append(Guid.NewGuid()), true);
        await _libraryService.SetSongsLovedAsync([songs[2].Id], false);

        var page = await _libraryReader.GetFavoritesPagedAsync("100%", 2, 1, SongSortOrder.TitleAsc);
        var ids = await _libraryReader.GetFavoriteSongIdsAsync("100%", SongSortOrder.TitleAsc);

        page.TotalCount.Should().Be(2);
        page.Items.Should().ContainSingle().Which.Id.Should().Be(ids[1]);
        ids.Should().BeEquivalentTo(songs.Take(2).Select(s => s.Id));
    }

    [Fact]
    public async Task Rediscovery_UsesEstablishedListeningHistoryAndPrioritizesOldFavorites()
    {
        var folder = new Folder { Name = "Music", Path = "C:\\Music" };
        var old = DateTime.UtcNow.AddDays(-120);
        Song[] songs = [
            new() { Folder = folder, Title = "Favorite", FilePath = "C:\\Music\\1.mp3", PlayCount = 3, LastPlayedDate = old, IsLoved = true },
            new() { Folder = folder, Title = "Popular", FilePath = "C:\\Music\\2.mp3", PlayCount = 20, LastPlayedDate = old },
            new() { Folder = folder, Title = "Recent", FilePath = "C:\\Music\\3.mp3", PlayCount = 100, LastPlayedDate = DateTime.UtcNow, IsLoved = true },
            new() { Folder = folder, Title = "Unheard", FilePath = "C:\\Music\\4.mp3", IsLoved = true }
        ];
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Songs.AddRange(songs);
            await context.SaveChangesAsync();
        }

        (await _libraryReader.GetRediscoverySuggestionAsync())!.SongId.Should().Be(songs[0].Id);
        await _libraryService.SetSongsLovedAsync([songs[0].Id], false);
        (await _libraryReader.GetRediscoverySuggestionAsync())!.SongId.Should().Be(songs[1].Id);
    }
}

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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

        var page = await _libraryService.GetFavoritesPagedAsync("100%", 2, 1, SongSortOrder.TitleAsc);
        var ids = await _libraryService.GetFavoriteSongIdsAsync("100%", SongSortOrder.TitleAsc);

        page.TotalCount.Should().Be(2);
        page.Items.Should().ContainSingle().Which.Id.Should().Be(ids[1]);
        ids.Should().BeEquivalentTo(songs.Take(2).Select(s => s.Id));
    }
}

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nagi.Core.Library;
using Nagi.Core.Listening;
using Nagi.Core.Playback;
using Nagi.Core.Tests.Utils;
using Xunit;

namespace Nagi.Core.Tests.Listening;

public class ListeningServiceTests : LibraryTestBase
{
    [Fact]
    public async Task StartListenSessionAsync_CreatesHistoryEntry_WithCorrectContext()
    {
        var folder = new Folder { Name = "Test Folder", Path = "C:\\Test" };
        var song = new Song { Title = "Test Song", Folder = folder, FilePath = "C:\\Test\\song.mp3" };

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Songs.Add(song);
            await context.SaveChangesAsync();
        }

        var playbackContext = new PlaybackContext(PlaybackContextType.Album, Guid.NewGuid());

        var sessionId = await _library.Listening.StartListenSessionAsync(song.Id, playbackContext);

        sessionId.Should().NotBeNull();

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var historyEntry = await assertContext.ListenHistory.FindAsync(sessionId);

        historyEntry.Should().NotBeNull();
        historyEntry!.SongId.Should().Be(song.Id);
        historyEntry.ContextType.Should().Be(PlaybackContextType.Album);
        historyEntry.ContextId.Should().Be(playbackContext.ContextId);
        historyEntry.ListenDurationTicks.Should().Be(0);
        historyEntry.EndReason.Should().Be(PlaybackEndReason.PausedAndAbandoned);
        historyEntry.IsEligibleForScrobbling.Should().BeFalse();
    }

    [Fact]
    public async Task StartListenSessionAsync_WithInvalidSongId_ReturnsNull()
    {
        var sessionId = await _library.Listening.StartListenSessionAsync(Guid.NewGuid(), new PlaybackContext(PlaybackContextType.Library, null));

        sessionId.Should().BeNull();
    }

    [Fact]
    public async Task FinalizeListenSessionAsync_WhenCalled_UpdatesDurationAndEndReason()
    {
        var folder = new Folder { Name = "Test Folder", Path = "C:\\Test" };
        var song = new Song { Title = "Test Song", Folder = folder, FilePath = "C:\\Test\\song.mp3", TotalListenTimeTicks = 1000 };
        var listenHistory = new ListenHistory { Song = song, EndReason = PlaybackEndReason.PausedAndAbandoned, ListenDurationTicks = 0, IsEligibleForScrobbling = true };

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Songs.Add(song);
            context.ListenHistory.Add(listenHistory);
            await context.SaveChangesAsync();
        }

        var finalDuration = TimeSpan.FromSeconds(150);

        await _library.Listening.FinalizeListenSessionAsync(listenHistory.Id, finalDuration, PlaybackEndReason.Finished);

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var updatedHistory = await assertContext.ListenHistory.FindAsync(listenHistory.Id);
        var updatedSong = await assertContext.Songs.FindAsync(song.Id);

        updatedHistory!.EndReason.Should().Be(PlaybackEndReason.Finished);
        updatedHistory.ListenDurationTicks.Should().Be(finalDuration.Ticks);

        updatedSong!.TotalListenTimeTicks.Should().Be(1000 + finalDuration.Ticks);
    }

    [Fact]
    public async Task FinalizeListenSessionAsync_WhenSkippedAndNotEligible_IncrementsSkipCount()
    {
        var folder = new Folder { Name = "Test Folder", Path = "C:\\Test" };
        var song = new Song { Title = "Test Song", Folder = folder, FilePath = "C:\\Test\\song.mp3", SkipCount = 2, TotalListenTimeTicks = 0 };

        var listenHistory = new ListenHistory { Song = song, EndReason = PlaybackEndReason.PausedAndAbandoned, ListenDurationTicks = 0, IsEligibleForScrobbling = false };

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Songs.Add(song);
            context.ListenHistory.Add(listenHistory);
            await context.SaveChangesAsync();
        }

        var finalDuration = TimeSpan.FromSeconds(10);

        await _library.Listening.FinalizeListenSessionAsync(listenHistory.Id, finalDuration, PlaybackEndReason.Skipped);
        await _library.Listening.FinalizeListenSessionAsync(listenHistory.Id, finalDuration, PlaybackEndReason.Skipped);

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var updatedHistory = await assertContext.ListenHistory.FindAsync(listenHistory.Id);
        var updatedSong = await assertContext.Songs.FindAsync(song.Id);

        updatedHistory!.EndReason.Should().Be(PlaybackEndReason.Skipped);
        updatedHistory.ListenDurationTicks.Should().Be(finalDuration.Ticks);

        updatedSong!.SkipCount.Should().Be(3);
        updatedSong.TotalListenTimeTicks.Should().Be(finalDuration.Ticks);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ListenCallbacks_PreserveCountsRegardlessOfEligibilityOrder(bool eligibilityFirst)
    {
        var song = new Song
        {
            Folder = new Folder { Path = @"C:\Music", Name = "Music" },
            FilePath = @"C:\Music\song.mp3",
            PlayCount = 5,
            SkipCount = 2
        };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Songs.Add(song);
            await context.SaveChangesAsync();
        }

        var id = (await _library.Listening.StartListenSessionAsync(song.Id, new PlaybackContext(PlaybackContextType.Library, null)))!.Value;
        var beforeEligible = DateTime.UtcNow;
        if (eligibilityFirst)
            await _library.Listening.MarkListenAsEligibleForScrobblingAsync(id);
        await _library.Listening.FinalizeListenSessionAsync(id, TimeSpan.FromMinutes(3), PlaybackEndReason.Skipped);
        (await _library.Listening.MarkListenAsEligibleForScrobblingAsync(id)).Should().BeTrue();
        await _library.Listening.MarkListenAsEligibleForScrobblingAsync(id);
        await _library.Listening.FinalizeListenSessionAsync(id, TimeSpan.FromMinutes(3), PlaybackEndReason.Skipped);

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        (await assertContext.ListenHistory.SingleAsync()).IsEligibleForScrobbling.Should().BeTrue();
        var updatedSong = await assertContext.Songs.SingleAsync();
        updatedSong.PlayCount.Should().Be(6);
        updatedSong.SkipCount.Should().Be(2);
        updatedSong.LastPlayedDate.Should().BeOnOrAfter(beforeEligible);
        updatedSong.TotalListenTimeTicks.Should().Be(TimeSpan.FromMinutes(3).Ticks);
    }

    [Fact]
    public async Task ListenCallbacks_ConcurrentSessionsPreserveCounters()
    {
        using var database = new DbContextFactoryTestHelper(useFileDatabase: true);
        using var library = CreateLibrary(database);
        var song = new Song { FilePath = @"C:\Music\song.mp3", Folder = new Folder { Path = @"C:\Music", Name = "Music" } };
        await using (var context = database.ContextFactory.CreateDbContext())
        {
            context.Songs.Add(song);
            await context.SaveChangesAsync();
        }
        var playbackContext = new PlaybackContext(PlaybackContextType.Library, null);
        var first = (await library.Listening.StartListenSessionAsync(song.Id, playbackContext))!.Value;
        var second = (await library.Listening.StartListenSessionAsync(song.Id, playbackContext))!.Value;

        await Task.WhenAll(new[] { first, second }.SelectMany(id => new Func<Task>[]
        {
            () => library.Listening.MarkListenAsEligibleForScrobblingAsync(id),
            () => library.Listening.MarkListenAsEligibleForScrobblingAsync(id),
            () => library.Listening.FinalizeListenSessionAsync(id, TimeSpan.FromSeconds(40), PlaybackEndReason.Skipped),
            () => library.Listening.FinalizeListenSessionAsync(id, TimeSpan.FromSeconds(40), PlaybackEndReason.Skipped)
        }).Select(callback => Task.Run(callback)));

        await using var assertContext = database.ContextFactory.CreateDbContext();
        var updated = await assertContext.Songs.SingleAsync();
        updated.PlayCount.Should().Be(2);
        updated.SkipCount.Should().Be(0);
        updated.TotalListenTimeTicks.Should().Be(TimeSpan.FromSeconds(80).Ticks);
        (await assertContext.ListenHistory.CountAsync(history => history.IsEligibleForScrobbling)).Should().Be(2);
    }

    [Fact]
    public async Task ListenCallbacks_ForMissingHistory_DoNotRecreateEntries()
    {
        await _library.Listening.FinalizeListenSessionAsync(99999L, TimeSpan.FromSeconds(10), PlaybackEndReason.Finished);
        (await _library.Listening.MarkListenAsEligibleForScrobblingAsync(99999L)).Should().BeFalse();
        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        (await context.ListenHistory.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ClearListenHistoryAsync_DeletesHistoryWithoutRemovingSongs()
    {
        var folder = new Folder { Name = "F", Path = "C:\\Music" };
        var song = new Song { Title = "S", Folder = folder, FilePath = "C:\\Music\\s.mp3" };
        await using (var ctx = _dbHelper.ContextFactory.CreateDbContext())
        {
            ctx.Folders.Add(folder);
            ctx.Songs.Add(song);
            ctx.ListenHistory.AddRange(
                new ListenHistory { Song = song },
                new ListenHistory { Song = song }
            );
            await ctx.SaveChangesAsync();
        }

        await _library.Listening.ClearListenHistoryAsync();

        await using var assertCtx = _dbHelper.ContextFactory.CreateDbContext();
        (await assertCtx.ListenHistory.CountAsync()).Should().Be(0, "history should be cleared");
        (await assertCtx.Songs.CountAsync()).Should().Be(1, "songs should not be deleted");
    }

    [Fact]
    public async Task Favorites_SearchPagingAndPlaybackIdsUseTheSameFilteredCollection()
    {
        var folder = new Folder { Name = "Music", Path = "C:\\Music" };
        Song[] songs = [
            new() { Folder = folder, Title = "Keep 100%", FilePath = "C:\\Music\\1.mp3" },
            new() { Folder = folder, Title = "Keep 100% again", FilePath = "C:\\Music\\2.mp3" },
            new() { Folder = folder, Title = "Other", FilePath = "C:\\Music\\3.mp3" },
            // Matches only if the % in the search were treated as a wildcard.
            new() { Folder = folder, Title = "Keep 1000", FilePath = "C:\\Music\\4.mp3" }
        ];
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Songs.AddRange(songs);
            await context.SaveChangesAsync();
        }
        await _library.Listening.SetSongsLovedAsync(songs.Select(s => s.Id).Append(Guid.NewGuid()), true);
        await _library.Listening.SetSongsLovedAsync([songs[2].Id], false);

        var page = await _libraryReader.GetSongsAsync(SongScope.Favorites, "100%", SongSortOrder.TitleAsc, 2, 1);
        var ids = await _libraryReader.GetSongIdsAsync(SongScope.Favorites, "100%", SongSortOrder.TitleAsc);

        page.TotalCount.Should().Be(2);
        page.Items.Should().ContainSingle().Which.Id.Should().Be(ids[1]);
        ids.Should().Equal(songs.Take(2).Select(s => s.Id));
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

        (await _library.Listening.GetRediscoverySuggestionAsync())!.SongId.Should().Be(songs[0].Id);
        await _library.Listening.SetSongsLovedAsync([songs[0].Id], false);
        (await _library.Listening.GetRediscoverySuggestionAsync())!.SongId.Should().Be(songs[1].Id);
    }

    [Fact]
    public async Task RecentListens_PreservesRepeatedListensAndStablePagingWhenTimestampsMatch()
    {
        var folder = new Folder { Name = "Music", Path = "C:\\Music" };
        var song = new Song { Folder = folder, Title = "Same song", ArtistName = "Artist", FilePath = "C:\\Music\\1.mp3" };
        var time = DateTime.UtcNow;
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Songs.Add(song);
            context.ListenHistory.AddRange(Enumerable.Range(0, 3).Select(_ => new ListenHistory { Song = song, ListenTimestampUtc = time }));
            await context.SaveChangesAsync();
        }

        var first = await _library.Listening.GetRecentListensAsync("Same song", 0, 2);
        var second = await _library.Listening.GetRecentListensAsync("Same song", 2, 2);

        first.Should().HaveCount(2);
        second.Should().ContainSingle();
        first.Concat(second).Select(h => h.Id).Should().BeInDescendingOrder().And.OnlyHaveUniqueItems();
        first.Concat(second).Should().OnlyContain(h => h.SongId == song.Id);
    }
}

using FluentAssertions;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.ViewModels;
using NSubstitute;
using Xunit;

namespace Nagi.Presentation.Tests;

public class InsightsViewModelTests
{
    [Theory]
    [InlineData("ru-RU", 1, "Воспроизведений: 1")]
    [InlineData("es-ES", 2, "Reproducciones: 2")]
    public async Task LocalizedPlayCounts_LoadInDashboardAndOverlay(string culture, int count, string expected)
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            var statistics = Substitute.For<IStatisticsService>();
            SongStats[] songs = [new(new Song { Title = "Song" }, count, TimeSpan.FromMinutes(2), 1)];
            ArtistStats[] artists = [new(new Artist { Name = "Artist" }, count, TimeSpan.FromMinutes(2), 1)];
            AlbumStats[] albums = [new(new Album { Title = "Album" }, count, TimeSpan.FromMinutes(2), 1)];
            GenreStats[] genres = [new(new Genre { Name = "Genre" }, count, TimeSpan.FromMinutes(2), 1)];
            statistics.GetListeningPatternsAsync(Arg.Any<TimeRange>(), Arg.Any<CancellationToken>())
                .Returns(new ListeningPatternStats(DayOfWeek.Monday, 12));
            statistics.GetTopSongsAsync(Arg.Any<TimeRange>(), Arg.Any<SortMetric>(), Arg.Any<int>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new StatisticsPage<SongStats>(songs, 1));
            statistics.GetTopArtistsAsync(Arg.Any<TimeRange>(), Arg.Any<SortMetric>(), Arg.Any<int>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new StatisticsPage<ArtistStats>(artists, 1));
            statistics.GetTopAlbumsAsync(Arg.Any<TimeRange>(), Arg.Any<SortMetric>(), Arg.Any<int>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new StatisticsPage<AlbumStats>(albums, 1));
            statistics.GetTopGenresAsync(Arg.Any<TimeRange>(), Arg.Any<SortMetric>(), Arg.Any<int>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new StatisticsPage<GenreStats>(genres, 1));
            var viewModel = CreateViewModel(statistics);
            viewModel.Artists.SetMetricCommand.Execute("PlayCount");

            await viewModel.LoadInsightsAsync();

            viewModel.HasData.Should().BeTrue();
            viewModel.Songs.TopItems.Should().ContainSingle().Which.StatText.Should().Be(expected);
            viewModel.Artists.TopItems.Should().ContainSingle().Which.StatText.Should().Be(expected);
            viewModel.Albums.TopItems.Should().ContainSingle().Which.StatText.Should().Be(expected);
            viewModel.Genres.TopItems.Should().ContainSingle().Which.StatText.Should().Be(expected);

            await viewModel.Songs.OpenSeeAllCommand.ExecuteAsync(null);
            viewModel.SeeAllItems.Should().ContainSingle().Which.StatText.Should().Be(expected);
            await viewModel.Artists.OpenSeeAllCommand.ExecuteAsync(null);
            viewModel.SeeAllItems.Should().ContainSingle().Which.StatText.Should().Be(expected);
            await viewModel.Albums.OpenSeeAllCommand.ExecuteAsync(null);
            viewModel.SeeAllItems.Should().ContainSingle().Which.StatText.Should().Be(expected);
            await viewModel.Genres.OpenSeeAllCommand.ExecuteAsync(null);
            viewModel.SeeAllItems.Should().ContainSingle().Which.StatText.Should().Be(expected);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DashboardReload_OldCompletionDoesNotOverwriteCurrentState(bool oldFailureArrivesLast)
    {
        var statistics = Substitute.For<IStatisticsService>();
        var oldResponse = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentResponse = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        statistics.GetTotalListenTimeAsync(Arg.Any<TimeRange>(), Arg.Any<CancellationToken>())
            .Returns(oldResponse.Task, currentResponse.Task);
        StubRankings(statistics, playedSongs: 1);
        statistics.GetListeningPatternsAsync(Arg.Any<TimeRange>(), Arg.Any<CancellationToken>())
            .Returns(new ListeningPatternStats(DayOfWeek.Monday, 12));
        var viewModel = CreateViewModel(statistics);
        var oldLoad = viewModel.LoadInsightsAsync();
        var currentLoad = viewModel.LoadInsightsAsync();
        try
        {
            if (oldFailureArrivesLast)
            {
                currentResponse.SetResult(TimeSpan.FromMinutes(3));
                await currentLoad;
                viewModel.HasData.Should().BeTrue();
                oldResponse.SetException(new IOException("Old request failed"));
                await oldLoad;
                viewModel.HasData.Should().BeTrue();
            }
            else
            {
                oldResponse.SetResult(TimeSpan.FromMinutes(1));
                await oldLoad;
                viewModel.IsLoading.Should().BeTrue();
            }
        }
        finally
        {
            oldResponse.TrySetResult(TimeSpan.Zero);
            currentResponse.TrySetResult(TimeSpan.FromMinutes(3));
            await Task.WhenAll(oldLoad, currentLoad);
        }
        viewModel.IsLoading.Should().BeFalse();
        viewModel.HasData.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SwitchingCategory_OldResponseDoesNotOverwriteCurrentLoadingState(bool oldRequestFails)
    {
        var statistics = Substitute.For<IStatisticsService>();
        var songs = new TaskCompletionSource<StatisticsPage<SongStats>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var artists = new TaskCompletionSource<StatisticsPage<ArtistStats>>(TaskCreationOptions.RunContinuationsAsynchronously);
        statistics.GetTopSongsAsync(Arg.Any<TimeRange>(), Arg.Any<SortMetric>(), Arg.Any<int>(),
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(songs.Task);
        statistics.GetTopArtistsAsync(Arg.Any<TimeRange>(), Arg.Any<SortMetric>(), Arg.Any<int>(),
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(artists.Task);
        var viewModel = CreateViewModel(statistics);

        var oldLoad = viewModel.Songs.OpenSeeAllCommand.ExecuteAsync(null);
        var currentLoad = viewModel.Artists.OpenSeeAllCommand.ExecuteAsync(null);
        try
        {
            if (oldRequestFails) songs.SetException(new IOException("Old request failed"));
            else songs.SetResult(new StatisticsPage<SongStats>(Array.Empty<SongStats>(), 200));
            await oldLoad;

            viewModel.SeeAllCategory.Should().BeSameAs(viewModel.Artists);
            viewModel.IsSeeAllLoading.Should().BeTrue();
            viewModel.SeeAllTotalPages.Should().Be(1);
        }
        finally
        {
            artists.TrySetResult(new StatisticsPage<ArtistStats>(Array.Empty<ArtistStats>(), 0));
            await currentLoad;
        }
        viewModel.IsSeeAllLoading.Should().BeFalse();
    }

    [Fact]
    public async Task ClosingOverlay_IgnoresLateResponse()
    {
        var statistics = Substitute.For<IStatisticsService>();
        var response = new TaskCompletionSource<StatisticsPage<SongStats>>(TaskCreationOptions.RunContinuationsAsynchronously);
        statistics.GetTopSongsAsync(Arg.Any<TimeRange>(), Arg.Any<SortMetric>(), Arg.Any<int>(),
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(response.Task);
        var viewModel = CreateViewModel(statistics);

        var load = viewModel.Songs.OpenSeeAllCommand.ExecuteAsync(null);
        await viewModel.CloseSeeAllCommand.ExecuteAsync(null);
        response.SetResult(new StatisticsPage<SongStats>(
            new[] { new SongStats(new Song { Title = "Stale song" }, 1, TimeSpan.Zero, 1) }, 200));
        await load;

        viewModel.IsSeeAllOpen.Should().BeFalse();
        viewModel.IsSeeAllLoading.Should().BeFalse();
        viewModel.SeeAllTotalPages.Should().Be(1);
        viewModel.SeeAllItems.Should().BeEmpty();
    }

    [Fact]
    public async Task FailedNextPage_PreservesDisplayedPageAndAllowsRetryAndPreviousPage()
    {
        var statistics = Substitute.For<IStatisticsService>();
        var first = new SongStats(new Song { Title = "First page" }, 1, TimeSpan.Zero, 1);
        var second = new SongStats(new Song { Title = "Second page" }, 1, TimeSpan.Zero, 26);
        statistics.GetTopSongsAsync(Arg.Any<TimeRange>(), Arg.Any<SortMetric>(), Arg.Is(0),
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new StatisticsPage<SongStats>(new[] { first }, 50));
        statistics.GetTopSongsAsync(Arg.Any<TimeRange>(), Arg.Any<SortMetric>(), Arg.Is<int>(offset => offset > 0),
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<StatisticsPage<SongStats>>(new IOException("Page unavailable")));
        var viewModel = CreateViewModel(statistics);
        await viewModel.Songs.OpenSeeAllCommand.ExecuteAsync(null);

        await viewModel.SeeAllNextPageCommand.ExecuteAsync(null);

        viewModel.SeeAllCurrentPage.Should().Be(1);
        viewModel.SeeAllItems.Should().ContainSingle().Which.Should().BeOfType<TopSongItem>().Which.Title.Should().Be("First page");
        viewModel.IsSeeAllLoading.Should().BeFalse();

        statistics.GetTopSongsAsync(Arg.Any<TimeRange>(), Arg.Any<SortMetric>(), Arg.Is<int>(offset => offset > 0),
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new StatisticsPage<SongStats>(new[] { second }, 50));
        await viewModel.SeeAllNextPageCommand.ExecuteAsync(null);
        viewModel.SeeAllCurrentPage.Should().Be(2);
        viewModel.SeeAllItems.Should().ContainSingle().Which.Should().BeOfType<TopSongItem>().Which.Title.Should().Be("Second page");

        await viewModel.SeeAllPreviousPageCommand.ExecuteAsync(null);
        viewModel.SeeAllCurrentPage.Should().Be(1);
        viewModel.SeeAllItems.Should().ContainSingle().Which.Should().BeOfType<TopSongItem>().Which.Title.Should().Be("First page");
    }

    [Fact]
    public async Task ChangingTheOpenCategorysMetric_ReloadsOverlayAndCard()
    {
        var statistics = Substitute.For<IStatisticsService>();
        StubRankings(statistics, playedSongs: 0);
        var viewModel = CreateViewModel(statistics);
        await viewModel.Albums.OpenSeeAllCommand.ExecuteAsync(null);
        statistics.ClearReceivedCalls();

        viewModel.Albums.SetMetricCommand.Execute("Duration");

        viewModel.Albums.IsByDuration.Should().BeTrue();
        await statistics.Received(1).GetTopAlbumsAsync(Arg.Any<TimeRange>(), SortMetric.Duration, 0, 25,
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await statistics.Received(1).GetTopAlbumsAsync(Arg.Any<TimeRange>(), SortMetric.Duration, 0, 10,
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
        viewModel.SeeAllCategory.Should().BeSameAs(viewModel.Albums);
    }

    private static void StubRankings(IStatisticsService statistics, int playedSongs)
    {
        statistics.GetTopSongsAsync(Arg.Any<TimeRange>(), Arg.Any<SortMetric>(), Arg.Any<int>(),
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new StatisticsPage<SongStats>([], playedSongs));
        statistics.GetTopArtistsAsync(Arg.Any<TimeRange>(), Arg.Any<SortMetric>(), Arg.Any<int>(),
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new StatisticsPage<ArtistStats>([], 0));
        statistics.GetTopAlbumsAsync(Arg.Any<TimeRange>(), Arg.Any<SortMetric>(), Arg.Any<int>(),
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new StatisticsPage<AlbumStats>([], 0));
        statistics.GetTopGenresAsync(Arg.Any<TimeRange>(), Arg.Any<SortMetric>(), Arg.Any<int>(),
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new StatisticsPage<GenreStats>([], 0));
    }

    private static InsightsViewModel CreateViewModel(IStatisticsService statistics) =>
        new(statistics, new ImmediateDispatcher(), Substitute.For<INavigationService>(),
            Substitute.For<ILibraryWriter>(), Substitute.For<IUIService>(),
            NullLogger<InsightsViewModel>.Instance);
}

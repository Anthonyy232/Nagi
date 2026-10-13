using FluentAssertions;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.ViewModels;
using NSubstitute;
using Xunit;

namespace Nagi.WinUI.Tests;

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
            SongStats[] songs = [new(new Song { Title = "Song" }, count, TimeSpan.FromMinutes(2), 0)];
            ArtistStats[] artists = [new(new Artist { Name = "Artist" }, count, TimeSpan.FromMinutes(2))];
            AlbumStats[] albums = [new(new Album { Title = "Album" }, count, TimeSpan.FromMinutes(2))];
            GenreStats[] genres = [new(new Genre { Name = "Genre" }, count, TimeSpan.FromMinutes(2))];
            statistics.GetListeningPatternsAsync(Arg.Any<TimeRange>(), Arg.Any<CancellationToken>())
                .Returns(new ListeningPatternStats(DayOfWeek.Monday, 12));
            statistics.GetTopSongsAsync(Arg.Any<TimeRange>(), Arg.Any<int>(), Arg.Any<SortMetric>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(songs);
            statistics.GetTopArtistsAsync(Arg.Any<TimeRange>(), Arg.Any<int>(), Arg.Any<SortMetric>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(artists);
            statistics.GetTopAlbumsAsync(Arg.Any<TimeRange>(), Arg.Any<int>(), Arg.Any<SortMetric>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(albums);
            statistics.GetTopGenresAsync(Arg.Any<TimeRange>(), Arg.Any<int>(), Arg.Any<SortMetric>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(genres);
            statistics.GetTopSongsPageAsync(Arg.Any<TimeRange>(), Arg.Any<int>(), Arg.Any<SortMetric>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new StatisticsPage<SongStats>(songs, 1));
            statistics.GetTopArtistsPageAsync(Arg.Any<TimeRange>(), Arg.Any<int>(), Arg.Any<SortMetric>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new StatisticsPage<ArtistStats>(artists, 1));
            statistics.GetTopAlbumsPageAsync(Arg.Any<TimeRange>(), Arg.Any<int>(), Arg.Any<SortMetric>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new StatisticsPage<AlbumStats>(albums, 1));
            statistics.GetTopGenresPageAsync(Arg.Any<TimeRange>(), Arg.Any<int>(), Arg.Any<SortMetric>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new StatisticsPage<GenreStats>(genres, 1));
            var viewModel = CreateViewModel(statistics);
            viewModel.ArtistsSortMetric = SortMetric.PlayCount;

            await viewModel.LoadInsightsAsync();

            viewModel.HasData.Should().BeTrue();
            viewModel.TopSongs.Should().ContainSingle().Which.StatText.Should().Be(expected);
            viewModel.TopArtists.Should().ContainSingle().Which.StatText.Should().Be(expected);
            viewModel.TopAlbums.Should().ContainSingle().Which.StatText.Should().Be(expected);
            viewModel.TopGenres.Should().ContainSingle().Which.StatText.Should().Be(expected);

            await viewModel.OpenSeeAllSongsCommand.ExecuteAsync(null);
            viewModel.SeeAllSongs.Should().ContainSingle().Which.StatText.Should().Be(expected);
            await viewModel.OpenSeeAllArtistsCommand.ExecuteAsync(null);
            viewModel.SeeAllArtists.Should().ContainSingle().Which.StatText.Should().Be(expected);
            await viewModel.OpenSeeAllAlbumsCommand.ExecuteAsync(null);
            viewModel.SeeAllAlbums.Should().ContainSingle().Which.StatText.Should().Be(expected);
            await viewModel.OpenSeeAllGenresCommand.ExecuteAsync(null);
            viewModel.SeeAllGenres.Should().ContainSingle().Which.StatText.Should().Be(expected);
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
        statistics.GetUniqueSongsPlayedAsync(Arg.Any<TimeRange>(), Arg.Any<CancellationToken>()).Returns(1);
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
        statistics.GetTopSongsPageAsync(Arg.Any<TimeRange>(), Arg.Any<int>(), Arg.Any<SortMetric>(),
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(songs.Task);
        statistics.GetTopArtistsPageAsync(Arg.Any<TimeRange>(), Arg.Any<int>(), Arg.Any<SortMetric>(),
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(artists.Task);
        var viewModel = CreateViewModel(statistics);

        var oldLoad = viewModel.OpenSeeAllSongsCommand.ExecuteAsync(null);
        var currentLoad = viewModel.OpenSeeAllArtistsCommand.ExecuteAsync(null);
        try
        {
            if (oldRequestFails) songs.SetException(new IOException("Old request failed"));
            else songs.SetResult(new StatisticsPage<SongStats>(Array.Empty<SongStats>(), 200));
            await oldLoad;

            viewModel.CurrentSeeAllCategory.Should().Be(SeeAllCategory.Artists);
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
        statistics.GetTopSongsPageAsync(Arg.Any<TimeRange>(), Arg.Any<int>(), Arg.Any<SortMetric>(),
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(response.Task);
        var viewModel = CreateViewModel(statistics);

        var load = viewModel.OpenSeeAllSongsCommand.ExecuteAsync(null);
        await viewModel.CloseSeeAllCommand.ExecuteAsync(null);
        response.SetResult(new StatisticsPage<SongStats>(
            new[] { new SongStats(new Song { Title = "Stale song" }, 1, TimeSpan.Zero, 0) }, 200));
        await load;

        viewModel.IsSeeAllOpen.Should().BeFalse();
        viewModel.IsSeeAllLoading.Should().BeFalse();
        viewModel.SeeAllTotalPages.Should().Be(1);
        viewModel.SeeAllSongs.Should().BeEmpty();
    }

    [Fact]
    public async Task FailedNextPage_PreservesDisplayedPageAndAllowsRetryAndPreviousPage()
    {
        var statistics = Substitute.For<IStatisticsService>();
        var first = new SongStats(new Song { Title = "First page" }, 1, TimeSpan.Zero, 0);
        var second = new SongStats(new Song { Title = "Second page" }, 1, TimeSpan.Zero, 0);
        statistics.GetTopSongsPageAsync(Arg.Any<TimeRange>(), Arg.Any<int>(), Arg.Any<SortMetric>(),
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new StatisticsPage<SongStats>(new[] { first }, 50));
        statistics.GetTopSongsAsync(Arg.Any<TimeRange>(), Arg.Any<int>(), Arg.Any<SortMetric>(),
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IEnumerable<SongStats>>(new IOException("Page unavailable")));
        var viewModel = CreateViewModel(statistics);
        await viewModel.OpenSeeAllSongsCommand.ExecuteAsync(null);

        await viewModel.SeeAllNextPageCommand.ExecuteAsync(null);

        viewModel.SeeAllCurrentPage.Should().Be(1);
        viewModel.SeeAllSongs.Should().ContainSingle().Which.Title.Should().Be("First page");
        viewModel.IsSeeAllLoading.Should().BeFalse();

        statistics.GetTopSongsAsync(Arg.Any<TimeRange>(), Arg.Any<int>(), Arg.Any<SortMetric>(),
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => new[] { call.ArgAt<int>(3) == 0 ? first : second });
        await viewModel.SeeAllNextPageCommand.ExecuteAsync(null);
        viewModel.SeeAllCurrentPage.Should().Be(2);
        viewModel.SeeAllSongs.Should().ContainSingle().Which.Title.Should().Be("Second page");

        await viewModel.SeeAllPreviousPageCommand.ExecuteAsync(null);
        viewModel.SeeAllCurrentPage.Should().Be(1);
        viewModel.SeeAllSongs.Should().ContainSingle().Which.Title.Should().Be("First page");
    }

    private static InsightsViewModel CreateViewModel(IStatisticsService statistics) =>
        new(statistics, new ImmediateDispatcher(), Substitute.For<INavigationService>(),
            Substitute.For<ILibraryWriter>(), Substitute.For<IUIService>(),
            NullLogger<InsightsViewModel>.Instance);
}

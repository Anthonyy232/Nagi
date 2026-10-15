using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

public class ReplayGainServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringAnalysis_PropagatesAndStopsFurtherSongs(bool scanLibrary)
    {
        using var database = new DbContextFactoryTestHelper();
        await using (var context = database.ContextFactory.CreateDbContext())
        {
            var folder = new Folder { Path = "C:\\Music" };
            context.Songs.AddRange(
                new Song { FilePath = "C:\\Music\\one.mp3", Folder = folder },
                new Song { FilePath = "C:\\Music\\two.mp3", Folder = folder });
            await context.SaveChangesAsync();
        }

        using var cancellation = new CancellationTokenSource();
        var analyzer = Substitute.For<ILoudnessAnalyzer>();
        analyzer.AnalyzeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<(double LoudnessLufs, double Peak)?>(cancellation.Token);
        });
        using var service = new ReplayGainService(database.ContextFactory, analyzer,
            NullLogger<ReplayGainService>.Instance);

        Func<Task> analyze = scanLibrary
            ? () => service.ScanLibraryAsync(cancellationToken: cancellation.Token)
            : () => service.CalculateAsync("C:\\Music\\one.mp3", cancellation.Token);

        await analyze.Should().ThrowAsync<OperationCanceledException>();
        await analyzer.Received(1).AnalyzeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await using var saved = database.ContextFactory.CreateDbContext();
        (await saved.Songs.CountAsync(song => song.ReplayGainTrackGain != null)).Should().Be(0);
    }

    [Fact]
    public async Task ScanLibraryAsync_DuringAPass_ReturnsAtOnceAndThePassRunsOneMoreRound()
    {
        using var database = new DbContextFactoryTestHelper();
        await using (var context = database.ContextFactory.CreateDbContext())
        {
            context.Songs.Add(new Song { FilePath = "C:\\Music\\one.mp3", Folder = new Folder { Path = "C:\\Music" } });
            await context.SaveChangesAsync();
        }

        var firstAnalysisStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstAnalysis = new TaskCompletionSource<(double LoudnessLufs, double Peak)?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var analyses = 0;
        var analyzer = Substitute.For<ILoudnessAnalyzer>();
        analyzer.AnalyzeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (Interlocked.Increment(ref analyses) > 1) return Task.FromResult<(double LoudnessLufs, double Peak)?>(null);
            firstAnalysisStarted.SetResult();
            return releaseFirstAnalysis.Task;
        });
        using var service = new ReplayGainService(database.ContextFactory, analyzer,
            NullLogger<ReplayGainService>.Instance);

        var firstPass = service.ScanLibraryAsync();
        await firstAnalysisStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.ScanLibraryAsync().WaitAsync(TimeSpan.FromSeconds(5));
        analyses.Should().Be(1);

        // The song still has no gain because analysis returns nothing, so the extra round analyzes it again.
        releaseFirstAnalysis.SetResult(null);
        await firstPass.WaitAsync(TimeSpan.FromSeconds(5));
        analyses.Should().Be(2);
    }
}

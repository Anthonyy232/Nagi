using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Library;
using Nagi.Core.Metadata;
using Nagi.Core.Playback;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests.Playback;

public class ReplayGainServiceTests
{
    [Fact]
    public async Task CancellationDuringAnalysis_PropagatesStopsFurtherSongsAndLetsALaterScanRun()
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
        using var service = new ReplayGainService(database.ContextFactory, analyzer, Substitute.For<IMetadataService>(),
            NullLogger<ReplayGainService>.Instance);

        Func<Task> analyze = () => service.ScanLibraryAsync(cancellationToken: cancellation.Token);

        await analyze.Should().ThrowAsync<OperationCanceledException>();
        await analyzer.Received(1).AnalyzeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await using (var saved = database.ContextFactory.CreateDbContext())
            (await saved.Songs.CountAsync(song => song.ReplayGainTrackGain != null)).Should().Be(0);

        // A cancelled pass that still counted as running would turn every later scan away.
        analyzer.AnalyzeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((-18.0, 0.5));
        await service.ScanLibraryAsync();

        await using var rescanned = database.ContextFactory.CreateDbContext();
        (await rescanned.Songs.CountAsync(song => song.ReplayGainTrackGain != null)).Should().Be(2);
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
        using var service = new ReplayGainService(database.ContextFactory, analyzer, Substitute.For<IMetadataService>(),
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

    [Fact]
    public async Task ScanLibraryAsync_LeavesServerSongsOut()
    {
        using var database = new DbContextFactoryTestHelper();
        await using (var context = database.ContextFactory.CreateDbContext())
        {
            context.Songs.AddRange(
                new Song { FilePath = "C:\\Music\\one.mp3", Folder = new Folder { Path = "C:\\Music" } },
                new Song { FilePath = "nagi-server:0123\\s1", RemoteId = "s1", Folder = new Folder { Path = "nagi-server:0123" } });
            await context.SaveChangesAsync();
        }
        var analyzer = Substitute.For<ILoudnessAnalyzer>();
        analyzer.AnalyzeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(double LoudnessLufs, double Peak)?>(null));
        using var service = new ReplayGainService(database.ContextFactory, analyzer, Substitute.For<IMetadataService>(),
            NullLogger<ReplayGainService>.Instance);

        await service.ScanLibraryAsync();

        await analyzer.Received(1).AnalyzeAsync("C:\\Music\\one.mp3", Arg.Any<CancellationToken>());
        await analyzer.DidNotReceive().AnalyzeAsync("nagi-server:0123\\s1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScanLibraryAsync_StoresTheGainAndTagsTheFile()
    {
        using var database = new DbContextFactoryTestHelper();
        await using (var context = database.ContextFactory.CreateDbContext())
        {
            context.Songs.Add(new Song { FilePath = "C:\\Music\\one.mp3", Folder = new Folder { Path = "C:\\Music" } });
            await context.SaveChangesAsync();
        }
        var analyzer = Substitute.For<ILoudnessAnalyzer>();
        analyzer.AnalyzeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(double LoudnessLufs, double Peak)?>((-14, 0.5)));
        var metadata = Substitute.For<IMetadataService>();
        using var service = new ReplayGainService(database.ContextFactory, analyzer, metadata,
            NullLogger<ReplayGainService>.Instance);

        await service.ScanLibraryAsync();

        await using var saved = database.ContextFactory.CreateDbContext();
        (await saved.Songs.SingleAsync()).ReplayGainTrackGain.Should().Be(-4);
        metadata.Received(1).WriteReplayGain("C:\\Music\\one.mp3", -4, 0.5);
    }
}

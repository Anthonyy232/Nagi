using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

public class OfflineScrobbleServiceTests
{
    private readonly ISettingsService _settingsService;

    public OfflineScrobbleServiceTests()
    {
        _settingsService = Substitute.For<ISettingsService>();
    }

    private OfflineScrobbleService CreateService(params IListenSubmitter[] submitters)
    {
        return new OfflineScrobbleService(
            submitters,
            _settingsService,
            NullLogger<OfflineScrobbleService>.Instance);
    }

    [Fact]
    public async Task ProcessQueueAsync_OnlyCallsEnabledSubmitters()
    {
        var enabled = Substitute.For<IListenSubmitter>();
        enabled.Id.Returns("x");
        enabled.IsEnabledAsync().Returns(true);

        var disabled = Substitute.For<IListenSubmitter>();
        disabled.Id.Returns("y");
        disabled.IsEnabledAsync().Returns(false);

        using var service = CreateService(enabled, disabled);

        await service.ProcessQueueAsync();

        await enabled.Received(1).ProcessPendingListensAsync(Arg.Any<CancellationToken>());
        await disabled.DidNotReceive().ProcessPendingListensAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessQueueAsync_OneSubmitterFailure_DoesNotBlockOthers(bool timeout)
    {
        var failing = Substitute.For<IListenSubmitter>();
        failing.Id.Returns("failing");
        failing.IsEnabledAsync().Returns(true);
        failing.ProcessPendingListensAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(timeout ? new TaskCanceledException("Request timed out")
                : new HttpRequestException("boom")));

        var healthy = Substitute.For<IListenSubmitter>();
        healthy.Id.Returns("healthy");
        healthy.IsEnabledAsync().Returns(true);

        using var service = CreateService(failing, healthy);

        await service.ProcessQueueAsync();

        await healthy.Received(1).ProcessPendingListensAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessQueueAsync_WhenCancelled_DoesNotCallSubmitters()
    {
        var submitter = Substitute.For<IListenSubmitter>();
        submitter.IsEnabledAsync().Returns(true);

        using var service = CreateService(submitter);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await service.ProcessQueueAsync(cts.Token);

        await submitter.DidNotReceive().ProcessPendingListensAsync(Arg.Any<CancellationToken>());
        await submitter.DidNotReceive().IsEnabledAsync();
    }

    [Fact]
    public async Task Dispose_CancelsAnExplicitQueueRun()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submitter = Substitute.For<IListenSubmitter>();
        submitter.IsEnabledAsync().Returns(true);
        submitter.ProcessPendingListensAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            started.SetResult();
            await release.Task.WaitAsync(call.Arg<CancellationToken>());
        });
        using var service = CreateService(submitter);
        var processing = service.ProcessQueueAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.Dispose();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            release.TrySetResult();
            try { await processing; }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task ProcessQueueAsync_WhenAlreadyRunning_ExitsImmediately()
    {
        var tcs = new TaskCompletionSource();
        var submitter = Substitute.For<IListenSubmitter>();
        submitter.Id.Returns("slow");
        submitter.IsEnabledAsync().Returns(true);
        submitter.ProcessPendingListensAsync(Arg.Any<CancellationToken>()).Returns(tcs.Task);

        using var service = CreateService(submitter);

        var firstCall = service.ProcessQueueAsync();
        var secondCall = service.ProcessQueueAsync();

        await secondCall;

        tcs.SetResult();
        await firstCall;

        await submitter.Received(1).ProcessPendingListensAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SettingsChanged_TriggersImmediateProcessing(bool lastFm)
    {
        var submitter = Substitute.For<IListenSubmitter>();
        submitter.IsEnabledAsync().Returns(true);

        using var service = CreateService(submitter);

        if (lastFm)
            _settingsService.LastFmSettingsChanged += Raise.Event<Action>();
        else
            _settingsService.ListenBrainzSettingsChanged += Raise.Event<Action>();

        await submitter.Received().ProcessPendingListensAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dispose_UnsubscribesFromBothSettingsChangedEvents()
    {
        var submitter = Substitute.For<IListenSubmitter>();
        submitter.IsEnabledAsync().Returns(true);

        using var service = CreateService(submitter);
        service.Dispose();

        _settingsService.LastFmSettingsChanged += Raise.Event<Action>();
        _settingsService.ListenBrainzSettingsChanged += Raise.Event<Action>();

        await submitter.DidNotReceive().IsEnabledAsync();
        await submitter.DidNotReceive().ProcessPendingListensAsync(Arg.Any<CancellationToken>());
    }

}

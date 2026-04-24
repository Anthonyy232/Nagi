using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

/// <summary>
///     Unit tests for <see cref="OfflineScrobbleService" /> in its fan-out orchestrator role.
///     The service's responsibility is now limited to: enforcing single-flight execution,
///     skipping disabled submitters, isolating per-submitter failures, responding to settings
///     events, and tracking consecutive-failure state for the backoff loop. Per-destination
///     DB-query + HTTP-submit behavior lives in the individual scrobbler services and is
///     tested there.
/// </summary>
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

    #region ProcessQueueAsync — submitter fan-out

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

    [Fact]
    public async Task ProcessQueueAsync_OneSubmitterFailure_DoesNotBlockOthers()
    {
        var failing = Substitute.For<IListenSubmitter>();
        failing.Id.Returns("failing");
        failing.IsEnabledAsync().Returns(true);
        failing.ProcessPendingListensAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new HttpRequestException("boom")));

        var healthy = Substitute.For<IListenSubmitter>();
        healthy.Id.Returns("healthy");
        healthy.IsEnabledAsync().Returns(true);

        using var service = CreateService(failing, healthy);

        await service.ProcessQueueAsync();

        await healthy.Received(1).ProcessPendingListensAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessQueueAsync_WithNoSubmitters_CompletesWithoutError()
    {
        using var service = CreateService();

        await service.ProcessQueueAsync();

        // No exceptions, nothing to assert against — service should be a no-op.
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
    public async Task ProcessQueueAsync_WhenAlreadyRunning_ExitsImmediately()
    {
        var tcs = new TaskCompletionSource();
        var submitter = Substitute.For<IListenSubmitter>();
        submitter.Id.Returns("slow");
        submitter.IsEnabledAsync().Returns(true);
        submitter.ProcessPendingListensAsync(Arg.Any<CancellationToken>()).Returns(tcs.Task);

        using var service = CreateService(submitter);

        // Start the first call (hangs on the submitter's task).
        var firstCall = service.ProcessQueueAsync();
        // Second call should hit the single-flight guard and return immediately.
        var secondCall = service.ProcessQueueAsync();

        await secondCall; // Must complete even though firstCall is hanging.

        // Release the first call's work.
        tcs.SetResult();
        await firstCall;

        // The core work should have been executed exactly once.
        await submitter.Received(1).ProcessPendingListensAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessQueueAsync_WhenIsEnabledThrows_ContinuesWithOtherSubmitters()
    {
        var throwing = Substitute.For<IListenSubmitter>();
        throwing.Id.Returns("throwing");
        throwing.IsEnabledAsync().Returns<Task<bool>>(_ => throw new InvalidOperationException("config broken"));

        var healthy = Substitute.For<IListenSubmitter>();
        healthy.Id.Returns("healthy");
        healthy.IsEnabledAsync().Returns(true);

        using var service = CreateService(throwing, healthy);

        await service.ProcessQueueAsync();

        await throwing.DidNotReceive().ProcessPendingListensAsync(Arg.Any<CancellationToken>());
        await healthy.Received(1).ProcessPendingListensAsync(Arg.Any<CancellationToken>());
    }

    #endregion

    #region Settings-change event wiring

    [Fact]
    public async Task OnLastFmSettingsChanged_TriggersImmediateProcessing()
    {
        var submitter = Substitute.For<IListenSubmitter>();
        submitter.IsEnabledAsync().Returns(true);

        using var service = CreateService(submitter);

        _settingsService.LastFmSettingsChanged += Raise.Event<Action>();
        await Task.Delay(100); // let the fire-and-forget start

        await submitter.Received().ProcessPendingListensAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnListenBrainzSettingsChanged_TriggersImmediateProcessing()
    {
        var submitter = Substitute.For<IListenSubmitter>();
        submitter.IsEnabledAsync().Returns(true);

        using var service = CreateService(submitter);

        _settingsService.ListenBrainzSettingsChanged += Raise.Event<Action>();
        await Task.Delay(100); // let the fire-and-forget start

        await submitter.Received().ProcessPendingListensAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dispose_UnsubscribesFromBothSettingsChangedEvents()
    {
        var submitter = Substitute.For<IListenSubmitter>();
        submitter.IsEnabledAsync().Returns(true);

        var service = CreateService(submitter);
        service.Dispose();

        // Raise BOTH events post-disposal; neither should trigger any submitter activity.
        _settingsService.LastFmSettingsChanged += Raise.Event<Action>();
        _settingsService.ListenBrainzSettingsChanged += Raise.Event<Action>();
        await Task.Delay(100);

        await submitter.DidNotReceive().IsEnabledAsync();
        await submitter.DidNotReceive().ProcessPendingListensAsync(Arg.Any<CancellationToken>());
    }

    #endregion

    #region Backoff state

    [Fact]
    public async Task ProcessQueueAsync_OnAllSubmittersSucceed_ResetsConsecutiveFailures()
    {
        var submitter = Substitute.For<IListenSubmitter>();
        submitter.IsEnabledAsync().Returns(true);

        using var service = CreateService(submitter);

        // Seed a non-zero failure count via reflection to simulate prior failures.
        var field = typeof(OfflineScrobbleService)
            .GetField("_consecutiveFailures", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(service, 3);

        await service.ProcessQueueAsync();

        Assert.Equal(0, (int)field.GetValue(service)!);
    }

    [Fact]
    public async Task ProcessQueueAsync_OnSubmitterFailure_IncrementsConsecutiveFailures()
    {
        var submitter = Substitute.For<IListenSubmitter>();
        submitter.IsEnabledAsync().Returns(true);
        submitter.ProcessPendingListensAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new HttpRequestException("boom")));

        using var service = CreateService(submitter);

        var field = typeof(OfflineScrobbleService)
            .GetField("_consecutiveFailures", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(service, 0);

        await service.ProcessQueueAsync();

        Assert.Equal(1, (int)field.GetValue(service)!);
    }

    #endregion
}

using FluentAssertions;
using Nagi.WinUI.Helpers;
using Xunit;

namespace Nagi.WinUI.Tests;

public class SingleInstanceManagerTests
{
    [Fact]
    public async Task SecondaryActivation_WaitsForListenerAndDeliversFile()
    {
        var instanceId = Guid.NewGuid().ToString("N");
        using var primary = new SingleInstanceManager(instanceId: instanceId);
        using var secondary = new SingleInstanceManager(instanceId: instanceId);
        var received = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var send = secondary.SendActivationAsync("C:\\Music\\song.mp3");

        primary.StartListening(path => received.TrySetResult(path));

        (await send.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        (await received.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be("C:\\Music\\song.mp3");
    }

    [Fact]
    public void TryAcquire_WhileAnotherThreadOwnsMutex_ReturnsFalse()
    {
        var instanceId = Guid.NewGuid().ToString("N");
        using var primary = new SingleInstanceManager(instanceId);
        primary.TryAcquire().Should().BeTrue();
        var acquired = true;
        var otherThread = new Thread(() =>
        {
            using var secondary = new SingleInstanceManager(instanceId);
            acquired = secondary.TryAcquire();
        });
        otherThread.Start();
        otherThread.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
        acquired.Should().BeFalse();
    }

    [Fact]
    public void TryAcquire_AfterPreviousOwnerThreadExits_AcquiresAbandonedMutex()
    {
        var instanceId = Guid.NewGuid().ToString("N");
        using var retainedHandle = new Mutex(false, $@"Global\NagiMusicPlayer-{instanceId}");
        var previousOwner = new Thread(() => retainedHandle.WaitOne());
        previousOwner.Start();
        previousOwner.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();

        using var manager = new SingleInstanceManager(instanceId: instanceId);
        manager.TryAcquire().Should().BeTrue();
    }
}

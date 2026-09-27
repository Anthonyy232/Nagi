using System.IO.Pipes;
using DiscordRPC.IO;
using FluentAssertions;
using Nagi.Core.Services.Implementations.Presence;
using Xunit;

namespace Nagi.WinUI.Tests;

public sealed class DiscordPipeClientTests : IDisposable
{
    private readonly NamedPipeServerStream _server;
    private readonly SandboxAwareDiscordPipeClient _client = new();

    public DiscordPipeClientTests()
    {
        var pipe = Random.Shared.Next(10000, int.MaxValue);
        _server = new NamedPipeServerStream($"LOCAL\\discord-ipc-{pipe}", PipeDirection.InOut,
            1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536);
        var connected = _server.WaitForConnectionAsync();
        _client.Connect(pipe).Should().BeTrue();
        connected.GetAwaiter().GetResult();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    public async Task ReadFrame_WithPartialFrame_WaitsForCompleteFrameWithoutBlocking(int split)
    {
        var expected = new PipeFrame { Opcode = Opcode.Frame, Data = [1, 2, 3, 4] };
        using var encoded = new MemoryStream();
        expected.WriteStream(encoded);
        var bytes = encoded.ToArray();
        await _server.WriteAsync(bytes.AsMemory(0, split));
        var firstRead = Task.Run(() => _client.ReadFrame(out _));
        Task<bool>? secondRead = null;
        try
        {
            (await firstRead.WaitAsync(TimeSpan.FromSeconds(2))).Should().BeFalse();
            await _server.WriteAsync(bytes.AsMemory(split));
            PipeFrame actual = default;
            secondRead = PollUntilAsync(() => _client.ReadFrame(out actual));
            (await secondRead.WaitAsync(TimeSpan.FromSeconds(2))).Should().BeTrue();
            actual.Opcode.Should().Be(expected.Opcode);
            actual.Data.Should().Equal(expected.Data);
        }
        finally
        {
            await _server.DisposeAsync();
            await firstRead.WaitAsync(TimeSpan.FromSeconds(2));
            if (secondRead is not null) await secondRead.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task ReadFrame_AfterServerDisconnects_MarksConnectionClosed()
    {
        _server.Dispose();

        await PollUntilAsync(() =>
        {
            _client.ReadFrame(out _).Should().BeFalse();
            return _client.ConnectedPipe == -1;
        }).WaitAsync(TimeSpan.FromSeconds(2));

        _client.IsConnected.Should().BeFalse();
        _client.ConnectedPipe.Should().Be(-1);
    }

    [Fact]
    public async Task ReadFrame_WithConsecutiveFrames_PreservesBoundaries()
    {
        var frames = new[]
        {
            new PipeFrame { Opcode = Opcode.Ping, Data = [] },
            new PipeFrame { Opcode = Opcode.Frame, Data = [1, 2, 3] }
        };
        using var encoded = new MemoryStream();
        foreach (var frame in frames) frame.WriteStream(encoded);
        await _server.WriteAsync(encoded.ToArray());

        foreach (var expected in frames)
        {
            PipeFrame actual = default;
            await PollUntilAsync(() => _client.ReadFrame(out actual)).WaitAsync(TimeSpan.FromSeconds(2));
            actual.Opcode.Should().Be(expected.Opcode);
            actual.Data.Should().Equal(expected.Data);
        }
        _client.ReadFrame(out _).Should().BeFalse();
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
    }

    [Fact]
    public async Task ReadFrame_LargerThanPipeBuffer_AssemblesCompletePayload()
    {
        var pipe = Random.Shared.Next(10000, int.MaxValue);
        await using var server = new NamedPipeServerStream($"LOCAL\\discord-ipc-{pipe}", PipeDirection.InOut,
            1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 512, 512);
        using var client = new SandboxAwareDiscordPipeClient();
        var connected = server.WaitForConnectionAsync();
        client.Connect(pipe).Should().BeTrue();
        await connected;
        var expected = new PipeFrame { Opcode = Opcode.Frame, Data = new byte[PipeFrame.MAX_SIZE] };
        Random.Shared.NextBytes(expected.Data);
        using var encoded = new MemoryStream();
        expected.WriteStream(encoded);

        var writing = server.WriteAsync(encoded.ToArray()).AsTask();
        PipeFrame actual = default;
        await Task.WhenAll(writing, PollUntilAsync(() => client.ReadFrame(out actual)))
            .WaitAsync(TimeSpan.FromSeconds(2));

        actual.Data.Should().Equal(expected.Data);
    }

    private static async Task<bool> PollUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition()) await Task.Delay(1, timeout.Token);
        return true;
    }
}

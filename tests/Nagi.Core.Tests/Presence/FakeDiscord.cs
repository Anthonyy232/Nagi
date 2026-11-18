using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;

namespace Nagi.Core.Tests.Presence;

/// <summary>
///     The Discord app's side of its IPC pipe, one connection at a time on a pipe name of its own.
/// </summary>
public sealed class FakeDiscord : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly List<NamedPipeServerStream> _servers = [];
    private NamedPipeServerStream? _current;

    public string PipeName { get; } = $"nagi-test-discord-{Guid.NewGuid():N}";

    /// <summary>
    ///     Listens at once, then waits for the client, answers its handshake with READY, and returns the handshake.
    /// </summary>
    public async Task<JsonElement> AcceptAsync()
    {
        // With no buffer, a client's write can wait until the test reads it.
        var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 2, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 65536, 65536);
        _servers.Add(server);
        await server.WaitForConnectionAsync().WaitAsync(Timeout);
        _current = server;
        var (opcode, handshake) = await ReadAsync();
        opcode.Should().Be(0);
        await SendAsync(1, """{"cmd":"DISPATCH","evt":"READY","data":{"v":1},"nonce":null}""");
        return handshake;
    }

    public async Task<(int Opcode, JsonElement Body)> ReadAsync()
    {
        var stream = _current!;
        var header = new byte[8];
        await stream.ReadExactlyAsync(header).AsTask().WaitAsync(Timeout);
        var body = new byte[BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4))];
        await stream.ReadExactlyAsync(body).AsTask().WaitAsync(Timeout);
        return (BinaryPrimitives.ReadInt32LittleEndian(header), JsonDocument.Parse(body).RootElement);
    }

    /// <summary>
    ///     Reads the next activity the client set.
    /// </summary>
    public async Task<string> ReadActivityAsync()
    {
        var (opcode, body) = await ReadAsync();
        opcode.Should().Be(1);
        body.GetProperty("cmd").GetString().Should().Be("SET_ACTIVITY");
        return body.GetProperty("args").GetProperty("activity").GetRawText();
    }

    public async Task SendAsync(int opcode, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var frame = new byte[8 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), body.Length);
        body.CopyTo(frame, 8);
        await _current!.WriteAsync(frame);
    }

    /// <summary>
    ///     Waits until the client closes the current connection.
    /// </summary>
    public async Task WaitForCloseAsync()
    {
        var read = await _current!.ReadAsync(new byte[1]).AsTask().WaitAsync(Timeout);
        read.Should().Be(0);
    }

    /// <summary>
    ///     Drops the current connection, as Discord does when it quits.
    /// </summary>
    public ValueTask DisconnectAsync() => _current!.DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        foreach (var server in _servers) await server.DisposeAsync();
    }
}

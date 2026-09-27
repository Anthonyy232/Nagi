using System.IO.Pipes;
using System.Buffers.Binary;
using DiscordRPC.IO;
using DiscordRPC.Logging;

namespace Nagi.Core.Services.Implementations.Presence;

/// <summary>
///     A custom <see cref="INamedPipeClient" /> that handles MSIX sandbox pipe virtualization
///     and safely implements non-blocking reads to prevent DiscordRPC deadlocks.
/// </summary>
public sealed class SandboxAwareDiscordPipeClient : INamedPipeClient
{
    private const string PipeNamePrefix = "discord-ipc-";
    private const string SandboxPrefix = "LOCAL\\";

    private NamedPipeClientStream? _stream;
    private Task<PipeFrame?>? _pendingFrame;
    private int _connectedPipe = -1;

    public ILogger Logger { get; set; } = new NullLogger();

    public bool IsConnected => _stream is { IsConnected: true };

    public int ConnectedPipe => _connectedPipe;

    public bool Connect(int pipe)
    {
        Close();
        if (pipe >= 0) return TryConnect(pipe);

        // Auto-discover: try pipes 0-9
        for (var i = 0; i < 10; i++)
        {
            if (TryConnect(i)) return true;
        }

        return false;
    }

    public bool ReadFrame(out PipeFrame frame)
    {
        frame = default;
        var stream = _stream;
        if (stream is null) return false;

        var pending = _pendingFrame ??= ReadNextFrameAsync(stream);
        if (!pending.IsCompleted) return false;
        _pendingFrame = null;
        if (pending.GetAwaiter().GetResult() is not { } completedFrame)
        {
            Close();
            return false;
        }

        frame = completedFrame;
        return true;
    }

    private async Task<PipeFrame?> ReadNextFrameAsync(Stream stream)
    {
        try
        {
            var header = new byte[8];
            await stream.ReadExactlyAsync(header).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            if (length > PipeFrame.MAX_SIZE)
                throw new InvalidDataException($"Discord frame exceeds {PipeFrame.MAX_SIZE} bytes.");

            var data = new byte[length];
            await stream.ReadExactlyAsync(data).ConfigureAwait(false);
            return new PipeFrame
            {
                Opcode = (Opcode)BinaryPrimitives.ReadUInt32LittleEndian(header),
                Data = data
            };
        }
        catch (Exception ex)
        {
            // Closing a connection also finishes any read that the RPC loop stopped polling.
            if (ReferenceEquals(_stream, stream)) Logger.Error($"Error reading frame: {ex.Message}");
            return null;
        }
    }

    public bool WriteFrame(PipeFrame frame)
    {
        if (_stream is not { IsConnected: true }) return false;

        try
        {
            frame.WriteStream(_stream);
            _stream.Flush();
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Error writing frame: {ex.Message}");
            return false;
        }
    }

    public void Close()
    {
        var stream = _stream;
        _stream = null;
        _pendingFrame = null;
        _connectedPipe = -1;
        stream?.Dispose();
    }

    public void Dispose() => Close();

    private bool TryConnect(int pipe)
    {
        var pipeName = PipeNamePrefix + pipe;

        if (TryConnectToPipe(SandboxPrefix + pipeName, pipe)) return true;

        return TryConnectToPipe(pipeName, pipe);
    }

    private bool TryConnectToPipe(string pipeName, int pipeNumber)
    {
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            Logger.Trace($"Attempting connection to pipe: {pipeName}");
            client.Connect(2000);
            Logger.Info($"Connected to pipe: {pipeName}");
            _stream = client;
            _connectedPipe = pipeNumber;
            return true;
        }
        catch (Exception ex)
        {
            client.Dispose();
            if (ex is not TimeoutException)
                Logger.Error($"Failed to connect to pipe {pipeName}: {ex.Message}");
            return false;
        }
    }
}

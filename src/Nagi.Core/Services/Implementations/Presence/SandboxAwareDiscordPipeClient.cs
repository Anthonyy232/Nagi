using System.IO.Pipes;
using System.Buffers.Binary;
using DiscordRPC.IO;
using DiscordRPC.Logging;

namespace Nagi.Core.Services.Implementations.Presence;

/// <summary>
///     A custom <see cref="INamedPipeClient" /> that finds Discord's pipe inside the MSIX sandbox and on Unix,
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
        foreach (var pipeName in GetPipeNames(pipe))
        {
            if (TryConnectToPipe(pipeName, pipe)) return true;
        }

        return false;
    }

    /// <summary>
    ///     On Windows, the sandbox name comes first because MSIX virtualizes the plain one. On Unix, Discord's
    ///     pipe is a socket in one of several temp folders (including Flatpak and Snap ones); only existing
    ///     sockets are tried, so that missing ones don't each wait for the connect timeout.
    /// </summary>
    private static IEnumerable<string> GetPipeNames(int pipe)
    {
        var pipeName = PipeNamePrefix + pipe;
        if (OperatingSystem.IsWindows()) return [SandboxPrefix + pipeName, pipeName];

        return PipeLocation.GetPipes(pipe).Where(path => Path.GetFileName(path) == pipeName && File.Exists(path));
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

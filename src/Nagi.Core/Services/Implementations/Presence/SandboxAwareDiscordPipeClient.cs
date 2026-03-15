using System.IO.Pipes;
using System.Runtime.InteropServices;
using DiscordRPC.IO;
using DiscordRPC.Logging;
using Microsoft.Win32.SafeHandles;

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
    private int _connectedPipe;

    public ILogger Logger { get; set; } = new NullLogger();

    public bool IsConnected => _stream is { IsConnected: true };

    public int ConnectedPipe => _connectedPipe;

    // Native Windows API to check if data is available in the pipe without blocking
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PeekNamedPipe(
        SafePipeHandle handle,
        byte[]? buffer,
        int bufferSize,
        ref int bytesRead,
        ref int totalBytesAvail,
        ref int bytesLeftThisMessage);

    public bool Connect(int pipe)
    {
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
        if (_stream is not { IsConnected: true }) return false;

        try
        {
            int bytesRead = 0, totalBytesAvail = 0, bytesLeftThisMessage = 0;
            
            // CRITICAL: We must not block infinitely. If we block here, the DiscordRPC 
            // writer queue starves and Presence updates will never be sent.
            bool peekSuccess = PeekNamedPipe(_stream.SafePipeHandle, null, 0, ref bytesRead, ref totalBytesAvail, ref bytesLeftThisMessage);
            
            if (!peekSuccess || totalBytesAvail == 0)
            {
                return false; // No data available. Return immediately so the writer thread can run.
            }

            // Data is available! We can safely read without blocking.
            frame = new PipeFrame();
            return frame.ReadStream(_stream);
        }
        catch (Exception ex)
        {
            Logger.Error($"Error reading frame: {ex.Message}");
            return false;
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
        _stream?.Dispose();
        _stream = null;
        _connectedPipe = -1;
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
        try
        {
            Logger.Trace($"Attempting connection to pipe: {pipeName}");
            var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            client.Connect(2000); 

            if (client.IsConnected)
            {
                Logger.Info($"Connected to pipe: {pipeName}");
                _stream = client;
                _connectedPipe = pipeNumber;
                return true;
            }

            client.Dispose();
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to connect to pipe {pipeName}: {ex.Message}");
            return false;
        }
    }
}

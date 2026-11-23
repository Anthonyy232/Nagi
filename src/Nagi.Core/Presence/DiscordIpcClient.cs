using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Nagi.Core.Presence;

/// <summary>
///     Sets the user's activity through the Discord app's local IPC pipe, and reconnects whenever Discord restarts.
/// </summary>
public sealed class DiscordIpcClient : IDisposable
{
    private static readonly string[] RuntimeDirVariables = ["XDG_RUNTIME_DIR", "TMPDIR", "TMP", "TEMP"];
    private const int MaxFrameSize = 16 * 1024;
    private static readonly TimeSpan MinRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _appId;
    private readonly ILogger _logger;
    private readonly Func<IEnumerable<string>> _pipeNames;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile Stream? _readyStream;
    private DiscordActivity? _activity;
    private DiscordActivity? _sentActivity;
    private int _nonce;
    private Task? _loop;

    public DiscordIpcClient(string appId, ILogger logger, Func<IEnumerable<string>>? pipeNames = null)
    {
        _appId = appId;
        _logger = logger;
        _pipeNames = pipeNames ?? DefaultPipeNames;
    }

    /// <summary>
    ///     Raised each time Discord accepts a connection, including after it restarts.
    /// </summary>
    public event Action? Ready;

    public void Start() => _loop ??= Task.Run(() => RunAsync(_stop.Token));

    /// <summary>
    ///     Shows the activity once Discord is connected; an activity identical to the one shown is not sent again.
    /// </summary>
    public Task SetActivityAsync(DiscordActivity activity)
    {
        _activity = activity;
        return SendActivityAsync();
    }

    // The loop then closes the pipe, which clears the activity in Discord.
    public void Dispose() => _stop.Cancel();

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var retryDelay = MinRetryDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            var accepted = false;
            if (Connect() is { } stream)
            {
                await using (stream)
                {
                    try
                    {
                        await RunSessionAsync(stream, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException or JsonException
                                                   or InvalidDataException or OperationCanceledException)
                    {
                        if (!cancellationToken.IsCancellationRequested)
                            _logger.LogInformation("Discord connection closed: {Reason}", ex.Message);
                    }
                    finally
                    {
                        accepted = _readyStream == stream;
                        _readyStream = null;
                    }
                }
            }

            // Discord was running a moment ago, so it is likely restarting: look for it again at once.
            if (accepted)
            {
                retryDelay = MinRetryDelay;
                continue;
            }

            try
            {
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, MaxRetryDelay.Ticks));
        }
    }

    private async Task RunSessionAsync(Stream stream, CancellationToken cancellationToken)
    {
        await WriteAsync(stream, Opcode.Handshake, JsonSerializer.Serialize(new { v = 1, client_id = _appId }))
            .ConfigureAwait(false);
        while (true)
        {
            var (opcode, payload) = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
            switch (opcode)
            {
                case Opcode.Frame:
                    using (var message = JsonDocument.Parse(payload))
                    {
                        var root = message.RootElement;
                        var evt = root.TryGetProperty("evt", out var e) ? e.GetString() : null;
                        if (evt == "READY")
                        {
                            _logger.LogInformation("Connected to Discord.");
                            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                            // A new connection starts with no activity shown, so the current one must be sent again.
                            _sentActivity = null;
                            _readyStream = stream;
                            _writeLock.Release();
                            Ready?.Invoke();
                        }
                        else if (evt == "ERROR")
                        {
                            _logger.LogWarning("Discord rejected a request: {Error}", root.GetProperty("data").GetRawText());
                        }
                    }

                    break;
                case Opcode.Ping:
                    await WriteAsync(stream, Opcode.Pong, payload).ConfigureAwait(false);
                    break;
                case Opcode.Close:
                    _logger.LogWarning("Discord closed the connection: {Reason}", payload);
                    return;
            }
        }
    }

    private async Task SendActivityAsync()
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_readyStream is not { } stream || _activity is not { } activity || activity == _sentActivity) return;

            await WriteFrameAsync(stream, Opcode.Frame, JsonSerializer.Serialize(new
            {
                cmd = "SET_ACTIVITY",
                args = new { pid = Environment.ProcessId, activity },
                nonce = (++_nonce).ToString()
            }, JsonOptions)).ConfigureAwait(false);
            _sentActivity = activity;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The read loop sees the same failure and reconnects, which sends the activity again.
            _logger.LogDebug("Could not send the Discord activity: {Reason}", ex.Message);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task WriteAsync(Stream stream, Opcode opcode, string payload)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await WriteFrameAsync(stream, opcode, payload).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task WriteFrameAsync(Stream stream, Opcode opcode, string payload)
    {
        var body = Encoding.UTF8.GetBytes(payload);
        var frame = new byte[8 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, (int)opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), body.Length);
        body.CopyTo(frame, 8);
        await stream.WriteAsync(frame).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static async Task<(Opcode, string)> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[8];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        if (length > MaxFrameSize) throw new InvalidDataException($"Discord frame exceeds {MaxFrameSize} bytes.");

        var body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        return ((Opcode)BinaryPrimitives.ReadInt32LittleEndian(header), Encoding.UTF8.GetString(body));
    }

    private Stream? Connect()
    {
        foreach (var pipeName in _pipeNames())
        {
            var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                // No wait: a missing pipe fails at once instead of holding up the others until a timeout.
                client.Connect(0);
                _logger.LogDebug("Connected to Discord pipe {Pipe}.", pipeName);
                return client;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                client.Dispose();
            }
        }

        return null;
    }

    /// <summary>
    ///     On Windows, the sandbox name comes first because MSIX virtualizes the plain one. On Unix, Discord's
    ///     pipe is a socket in one of several temp folders (including Flatpak and Snap ones); only existing
    ///     sockets are tried.
    /// </summary>
    private static IEnumerable<string> DefaultPipeNames()
    {
        string[] folders = OperatingSystem.IsWindows()
            ? []
            : [
                ..RuntimeDirVariables
                    .Select(Environment.GetEnvironmentVariable).OfType<string>().Where(path => path.Length > 0),
                "/tmp"
            ];

        for (var pipe = 0; pipe < 10; pipe++)
        {
            var pipeName = "discord-ipc-" + pipe;
            if (OperatingSystem.IsWindows())
            {
                yield return "LOCAL\\" + pipeName;
                yield return pipeName;
                continue;
            }

            foreach (var folder in folders)
                foreach (var subfolder in new[] { "", "app/com.discordapp.Discord", "snap.discord" })
                {
                    var path = Path.Combine(folder, subfolder, pipeName);
                    if (File.Exists(path)) yield return path;
                }
        }
    }

    private enum Opcode
    {
        Handshake = 0,
        Frame = 1,
        Close = 2,
        Ping = 3,
        Pong = 4
    }
}

public sealed record DiscordActivity
{
    public string? State { get; init; }
    public string? Details { get; init; }
    public DiscordTimestamps? Timestamps { get; init; }
    public DiscordAssets? Assets { get; init; }

    // Listening.
    public int Type { get; } = 2;

    public DiscordStatusDisplay StatusDisplayType { get; init; }
}

/// <summary>
///     Unix times in milliseconds.
/// </summary>
public sealed record DiscordTimestamps(long Start, long? End);

public sealed record DiscordAssets(string LargeImage, string LargeText, string SmallImage, string SmallText);

public enum DiscordStatusDisplay
{
    Name = 0,
    Details = 2
}

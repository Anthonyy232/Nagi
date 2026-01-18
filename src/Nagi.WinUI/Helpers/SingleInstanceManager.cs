using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Nagi.WinUI.Helpers;

/// <summary>
///     Manages single-instance enforcement using Mutex and Named Pipes for inter-process communication.
///     This approach works reliably for both packaged (MSIX) and unpackaged (Velopack) builds.
/// </summary>
internal sealed class SingleInstanceManager : IDisposable
{
    private const string MutexName = @"Global\NagiMusicPlayer-9A8B7C6D";
    private const string PipeName = "NagiMusicPlayer-Activation-9A8B7C6D";
    private const int PipeTimeoutMs = 3000;

    private readonly ILogger<SingleInstanceManager>? _logger;
    private Mutex? _mutex;
    private CancellationTokenSource? _pipeServerCts;
    private Task? _pipeServerTask;
    private bool _isDisposed;

    public SingleInstanceManager(ILogger<SingleInstanceManager>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    ///     Raised when an activation message is received from a secondary instance.
    /// </summary>
    public event Action<string?>? ActivationReceived;

    /// <summary>
    ///     Attempts to acquire single-instance ownership.
    /// </summary>
    /// <returns>True if this is the primary instance; false if another instance exists.</returns>
    public bool TryAcquire()
    {
        try
        {
            _mutex = new Mutex(true, MutexName, out var createdNew);

            if (createdNew)
            {
                _logger?.LogInformation("Single instance acquired, starting as primary instance");
                StartPipeServer();
                return true;
            }

            _logger?.LogInformation("Existing instance detected");
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to acquire mutex, continuing as primary instance");
            // If mutex creation fails, assume we're the primary to avoid blocking the app
            return true;
        }
    }

    /// <summary>
    ///     Sends an activation message to the existing primary instance.
    /// </summary>
    /// <param name="filePath">Optional file path from activation arguments.</param>
    /// <returns>True if message was sent successfully; false otherwise.</returns>
    public async Task<bool> SendActivationAsync(string? filePath)
    {
        try
        {
            _logger?.LogDebug("Sending activation message to primary instance. FilePath: {FilePath}", filePath ?? "None");

            await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            await pipe.ConnectAsync(PipeTimeoutMs);

            var message = new ActivationMessage { FilePath = filePath };
            var json = JsonSerializer.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(json);

            await pipe.WriteAsync(bytes, 0, bytes.Length);
            await pipe.FlushAsync();

            _logger?.LogInformation("Activation message sent successfully");
            return true;
        }
        catch (TimeoutException)
        {
            _logger?.LogWarning("Timeout connecting to primary instance pipe");
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to send activation message to primary instance");
            return false;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        _logger?.LogDebug("Disposing SingleInstanceManager");

        // Stop the pipe server
        _pipeServerCts?.Cancel();
        _pipeServerTask?.Wait(TimeSpan.FromSeconds(2));
        _pipeServerCts?.Dispose();

        // Release the mutex
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();

        _isDisposed = true;
    }

    /// <summary>
    ///     Starts the Named Pipe server to listen for activation messages from secondary instances.
    /// </summary>
    private void StartPipeServer()
    {
        _pipeServerCts = new CancellationTokenSource();
        _pipeServerTask = Task.Run(async () => await RunPipeServerAsync(_pipeServerCts.Token));
    }

    private async Task RunPipeServerAsync(CancellationToken cancellationToken)
    {
        _logger?.LogDebug("Starting Named Pipe server on pipe: {PipeName}", PipeName);

        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(cancellationToken);
                _logger?.LogDebug("Secondary instance connected to pipe");

                // Read the activation message
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                var json = await reader.ReadToEndAsync();

                if (!string.IsNullOrEmpty(json))
                {
                    var message = JsonSerializer.Deserialize<ActivationMessage>(json);
                    _logger?.LogInformation("Received activation from secondary instance. FilePath: {FilePath}",
                        message?.FilePath ?? "None");

                    // Raise the event on a background thread (caller will dispatch to UI thread)
                    ActivationReceived?.Invoke(message?.FilePath);
                }
            }
            catch (OperationCanceledException)
            {
                _logger?.LogDebug("Pipe server cancelled");
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error in pipe server");
            }
            finally
            {
                pipe?.Dispose();
            }
        }

        _logger?.LogDebug("Named Pipe server stopped");
    }

    private sealed class ActivationMessage
    {
        public string? FilePath { get; set; }
    }
}

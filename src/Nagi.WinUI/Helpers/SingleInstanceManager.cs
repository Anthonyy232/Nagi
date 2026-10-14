using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Nagi.WinUI.Helpers;

internal sealed class SingleInstanceManager : IDisposable
{
    private readonly string _mutexName;
    private readonly string _pipeName;
    private Mutex? _mutex;
    private CancellationTokenSource? _pipeServerCts;
    private Task? _pipeServerTask;
    private bool _isDisposed;
    private bool _hasOwnership;

    public SingleInstanceManager(string instanceId = "9A8B7C6D")
    {
        _mutexName = $@"Global\NagiMusicPlayer-{instanceId}";
        _pipeName = $"NagiMusicPlayer-Activation-{instanceId}";
    }

    public bool TryAcquire()
    {
        _mutex = new Mutex(false, _mutexName);
        try
        {
            return _hasOwnership = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            return _hasOwnership = true;
        }
    }

    public async Task<bool> SendActivationAsync(string? filePath, JumpListTask task, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            await pipe.ConnectAsync(3000, cancellationToken);
            await JsonSerializer.SerializeAsync(pipe, new ActivationMessage { FilePath = filePath, JumpListTask = task },
                cancellationToken: cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to send activation to the running instance.");
            return false;
        }
    }

    // Start only after the App exists. Secondary instances wait in ConnectAsync until then.
    public void StartListening(Action<string?, JumpListTask> onActivation)
    {
        _pipeServerCts = new CancellationTokenSource();
        var token = _pipeServerCts.Token;
        _pipeServerTask = Task.Run(() => RunPipeServerAsync(onActivation, token));
    }

    private async Task RunPipeServerAsync(Action<string?, JumpListTask> onActivation, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                try
                {
                    await pipe.WaitForConnectionAsync(cancellationToken);
                    var message = await JsonSerializer.DeserializeAsync<ActivationMessage>(pipe,
                        cancellationToken: cancellationToken);
                    if (message is not null) onActivation(message.FilePath, message.JumpListTask);
                }
                catch (Exception ex) when (ex is IOException or JsonException)
                {
                    Log.Warning(ex, "Failed to read activation message.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Activation listener stopped unexpectedly.");
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _pipeServerCts?.Cancel();
        _pipeServerTask?.GetAwaiter().GetResult();
        _pipeServerCts?.Dispose();
        if (_hasOwnership) _mutex!.ReleaseMutex();
        _mutex?.Dispose();
    }

    private sealed class ActivationMessage
    {
        public string? FilePath { get; set; }
        public JumpListTask JumpListTask { get; set; }
    }
}

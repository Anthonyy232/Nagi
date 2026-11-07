using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Nagi.Core.Library;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Services.Abstractions;

namespace Nagi.Presentation.Services;

/// <summary>
///     Runs the app's launch: starts the services, shows the main shell or onboarding and then the windows the
///     settings ask for. Files and commands that this launch or a later one asks for wait until the services are up.
/// </summary>
public sealed class LaunchCoordinator
{
    private readonly ConcurrentQueue<string> _queuedFiles = new();
    private readonly IServiceProvider _services;
    private readonly IWindowService _windowService;
    private readonly WindowBehavior _windowBehavior;
    private readonly IApplicationLifecycle _applicationLifecycle;
    private readonly ILibraryReader _libraryReader;
    private readonly IMusicPlaybackService _playbackService;
    private readonly ThemeCoordinator _themeCoordinator;
    private readonly IDispatcherService _dispatcherService;
    private readonly ILogger<LaunchCoordinator> _logger;
    private volatile bool _isActivatedExternally;
    private LaunchCommand _pendingCommand;
    private bool _isReady;
    private bool _isPlayingQueuedFiles;

    public LaunchCoordinator(IServiceProvider services, IWindowService windowService, WindowBehavior windowBehavior,
        IApplicationLifecycle applicationLifecycle, ILibraryReader libraryReader, IMusicPlaybackService playbackService,
        ThemeCoordinator themeCoordinator, IDispatcherService dispatcherService, ILogger<LaunchCoordinator> logger)
    {
        _services = services;
        _windowService = windowService;
        _windowBehavior = windowBehavior;
        _applicationLifecycle = applicationLifecycle;
        _libraryReader = libraryReader;
        _playbackService = playbackService;
        _themeCoordinator = themeCoordinator;
        _dispatcherService = dispatcherService;
        _logger = logger;
    }

    /// <summary>
    ///     Plays <paramref name="filePath" /> and runs <paramref name="command" /> once the services are up, and shows
    ///     the main window unless the request keeps the mini player. Call it from any thread, for this launch's own
    ///     request before <see cref="StartAsync" /> and for every request another instance forwards.
    /// </summary>
    public void Activate(string? filePath, LaunchCommand command)
    {
        _isActivatedExternally = true;
        if (!string.IsNullOrEmpty(filePath)) _queuedFiles.Enqueue(filePath);
        if (command != LaunchCommand.None) _pendingCommand = command;

        _dispatcherService.TryEnqueue(() =>
        {
            if (!_isReady || _windowService.IsExiting) return;

            _ = PlayQueuedFilesAsync();
            try
            {
                // The mini player command replaces the main window, and a file opened while the mini player is up
                // plays there without disrupting it.
                var staysInMiniPlayer = command == LaunchCommand.MiniPlayer ||
                                        (!string.IsNullOrEmpty(filePath) && _windowService.IsMiniPlayerActive);
                if (!staysInMiniPlayer) _windowService.ShowAndActivate();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to show the window for an activation");
            }

            _ = RunPendingCommandAsync();
        });
    }

    /// <summary>
    ///     Call once on the UI thread, after this launch's own request went to <see cref="Activate" />.
    /// </summary>
    public async Task StartAsync()
    {
        _windowBehavior.ShowWhileLoading();

        // A launch that opens a file plays it instead of the last session.
        await AppServices.StartAsync(_services, restoreSession: _queuedFiles.IsEmpty, _logger);
        if (_windowService.IsExiting) return;
        _isReady = true;

        await ShowMainContentAsync();
        if (_windowService.IsExiting) return;
        _themeCoordinator.Start();
        AppServices.StartAfterMainContent(_services);

        _ = PlayQueuedFilesAsync();
        _windowBehavior.ShowWhenLoaded(_isActivatedExternally);
        await RunPendingCommandAsync();
    }

    /// <summary>
    ///     Shows the main shell when a library folder exists, or onboarding when there is none.
    /// </summary>
    private async Task ShowMainContentAsync()
    {
        _logger.LogInformation("Showing the main content.");
        var hasFolders = await _libraryReader.HasAnyFolderAsync();
        if (_windowService.IsExiting) return;

        if (hasFolders) _applicationLifecycle.ShowMainShell();
        else _applicationLifecycle.ShowOnboarding();
    }

    private async Task RunPendingCommandAsync()
    {
        if (!_isReady || _windowService.IsExiting) return;
        var command = Interlocked.Exchange(ref _pendingCommand, LaunchCommand.None);

        try
        {
            switch (command)
            {
                case LaunchCommand.MiniPlayer:
                    // The window behaviour swaps a minimized main window for the mini player.
                    _windowService.ShowMinimized();
                    break;
                case LaunchCommand.ShuffleAll:
                    await _playbackService.ShuffleLibraryAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run launch command {LaunchCommand}", command);
        }
    }

    private async Task PlayQueuedFilesAsync()
    {
        if (_isPlayingQueuedFiles) return;

        _isPlayingQueuedFiles = true;
        try
        {
            while (!_windowService.IsExiting && _queuedFiles.TryDequeue(out var filePath))
            {
                try
                {
                    await _playbackService.PlayTransientFileAsync(filePath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process file '{FilePath}'", filePath);
                }
            }
        }
        finally
        {
            _isPlayingQueuedFiles = false;
        }
    }
}

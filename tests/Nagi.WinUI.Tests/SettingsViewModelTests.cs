using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.WinUI.Models;
using Nagi.WinUI.Services.Abstractions;
using Nagi.WinUI.ViewModels;
using NSubstitute;
using Xunit;

namespace Nagi.WinUI.Tests;

public class SettingsViewModelTests : IDisposable
{
    private readonly IUISettingsService _settings = Substitute.For<IUISettingsService>();
    private readonly IThemeService _theme = Substitute.For<IThemeService>();
    private readonly IMusicPlaybackService _playback = Substitute.For<IMusicPlaybackService>();
    private readonly IAudioPlayer _audioPlayer = Substitute.For<IAudioPlayer>();
    private readonly IAppInfoService _appInfo = Substitute.For<IAppInfoService>();
    private readonly IUIService _ui = Substitute.For<IUIService>();
    private readonly IDispatcherService _dispatcher = Substitute.For<IDispatcherService>();
    private readonly ILastFmAuthService _lastFm = Substitute.For<ILastFmAuthService>();
    private readonly IListenBrainzScrobblerService _listenBrainz = Substitute.For<IListenBrainzScrobblerService>();
    private readonly ILibraryService _library = Substitute.For<ILibraryService>();
    private readonly IFFmpegService _ffmpeg = Substitute.For<IFFmpegService>();
    private readonly IReplayGainService _replayGain = Substitute.For<IReplayGainService>();
    private readonly PlayerViewModel _player;
    private readonly SettingsViewModel _viewModel;
    private readonly FakeTimeProvider _timeProvider = new();

    public SettingsViewModelTests()
    {
        _settings.GetPlayerButtonSettingsAsync().Returns(new List<PlayerButtonSetting>());
        _settings.GetNavigationItemsAsync().Returns(new List<Nagi.WinUI.Navigation.NavigationItemSetting>());
        _settings.GetServiceProvidersAsync(Arg.Any<ServiceCategory>()).Returns(new List<ServiceProviderSetting>());
        _settings.GetQueueDisplaySettingsAsync().Returns(new QueueDisplaySettings());
        _playback.PlaybackQueue.Returns(Array.Empty<Guid>());
        _playback.AvailablePresets.Returns(Array.Empty<EqualizerPreset>());
        _dispatcher.HasThreadAccess.Returns(true);
        _dispatcher.TryEnqueue(Arg.Any<Action>()).Returns(call => { call.Arg<Action>()(); return true; });
        _dispatcher.EnqueueAsync(Arg.Any<Func<Task>>()).Returns(call => call.Arg<Func<Task>>()());
        _player = new PlayerViewModel(_playback, Substitute.For<IMusicNavigationService>(), _dispatcher,
            _settings, Substitute.For<IWindowService>(), _library, NullLogger<PlayerViewModel>.Instance, Substitute.For<IUIService>());
        _viewModel = new SettingsViewModel(_settings, _ui, _theme,
            Substitute.For<IApplicationLifecycle>(), _appInfo,
            _lastFm, _listenBrainz,
            _playback, _audioPlayer, Substitute.For<IPlaylistExportService>(), _library, _player,
            _replayGain, _ffmpeg,
            Substitute.For<IBackupRestoreService>(), _dispatcher, Substitute.For<IRomanizationPackManager>(),
            NullLogger<SettingsViewModel>.Instance, _timeProvider);
    }

    public void Dispose()
    {
        _viewModel.Dispose();
        _player.Cleanup();
    }

    [Fact]
    public async Task AudioOutput_MissingSavedDeviceDisplaysDefaultWithoutLosingPreference()
    {
        _audioPlayer.GetAudioOutputDevicesAsync().Returns(Array.Empty<AudioOutputDeviceInfo>());
        _settings.GetAudioOutputDeviceIdAsync().Returns("disconnected-dac");

        await _viewModel.RefreshAudioOutputDevicesCommand.ExecuteAsync(null);

        _viewModel.SelectedAudioOutputDevice!.Id.Should().BeEmpty();
        _viewModel.AudioOutputDeviceMessage.Should().NotBeEmpty();
        await _settings.DidNotReceive().SetAudioOutputDeviceIdAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task AudioOutput_ReconnectedDeviceDoesNotMisrepresentVlcFallbackAsAnActiveSelection()
    {
        var device = new AudioOutputDeviceInfo("dac", "DAC");
        _settings.GetAudioOutputDeviceIdAsync().Returns(device.Id);
        _audioPlayer.AudioOutputDeviceId.Returns(string.Empty);
        _audioPlayer.GetAudioOutputDevicesAsync().Returns(new[] { device });
        _audioPlayer.SetAudioOutputDeviceAsync(device.Id).Returns(true);

        await _viewModel.RefreshAudioOutputDevicesCommand.ExecuteAsync(null);
        _viewModel.SelectedAudioOutputDevice!.Id.Should().BeEmpty();
        await _viewModel.SelectAudioOutputDeviceCommand.ExecuteAsync(device);

        await _audioPlayer.Received(1).SetAudioOutputDeviceAsync(device.Id);
    }

    [Fact]
    public async Task AudioOutput_RejectedSelectionRetainsPreviousDeviceAndPreference()
    {
        var previous = new AudioOutputDeviceInfo("speakers", "Speakers");
        _viewModel.SelectedAudioOutputDevice = previous;
        var selectionNotifications = 0;
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.SelectedAudioOutputDevice)) selectionNotifications++;
        };

        await _viewModel.SelectAudioOutputDeviceCommand.ExecuteAsync(new AudioOutputDeviceInfo("removed", "Removed"));

        _viewModel.SelectedAudioOutputDevice.Should().Be(previous);
        selectionNotifications.Should().BeGreaterThan(0);
        _viewModel.AudioOutputDeviceMessage.Should().NotBeEmpty();
        _viewModel.IsAudioOutputBusy.Should().BeFalse();
        await _settings.DidNotReceive().SetAudioOutputDeviceIdAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task AudioOutput_ChangedDeviceListKeepsSelectionInCurrentCollection()
    {
        _audioPlayer.AudioOutputDeviceId.Returns("dac");
        _audioPlayer.GetAudioOutputDevicesAsync().Returns(new[] { new AudioOutputDeviceInfo("dac", "DAC") });
        _settings.GetAudioOutputDeviceIdAsync().Returns("dac");
        await _viewModel.RefreshAudioOutputDevicesCommand.ExecuteAsync(null);

        _audioPlayer.GetAudioOutputDevicesAsync().Returns(new[]
        {
            new AudioOutputDeviceInfo("dac", "DAC"), new AudioOutputDeviceInfo("speakers", "Speakers")
        });
        await _viewModel.RefreshAudioOutputDevicesCommand.ExecuteAsync(null);

        _viewModel.SelectedAudioOutputDevice.Should().BeSameAs(_viewModel.AudioOutputDevices.Single(d => d.Id == "dac"));
        await _audioPlayer.DidNotReceive().SetAudioOutputDeviceAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task AudioOutput_UserSelectionDuringRefreshIsAppliedWithoutRefreshOverwritingIt()
    {
        var device = new AudioOutputDeviceInfo("dac", "DAC");
        var enumeration = new TaskCompletionSource<IReadOnlyList<AudioOutputDeviceInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var selection = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _audioPlayer.GetAudioOutputDevicesAsync().Returns(enumeration.Task);
        _audioPlayer.SetAudioOutputDeviceAsync(device.Id).Returns(selection.Task);
        var defaultDevice = new AudioOutputDeviceInfo(string.Empty, "Default");
        _viewModel.AudioOutputDevices = new[] { defaultDevice, device };
        _viewModel.SelectedAudioOutputDevice = defaultDevice;

        var refreshing = _viewModel.RefreshAudioOutputDevicesCommand.ExecuteAsync(null);
        var selecting = _viewModel.SelectAudioOutputDeviceCommand.ExecuteAsync(device);
        enumeration.SetResult(new[] { device });
        await refreshing.WaitAsync(TimeSpan.FromSeconds(5));
        _viewModel.SelectedAudioOutputDevice.Id.Should().BeEmpty();
        selection.SetResult(true);
        await selecting.WaitAsync(TimeSpan.FromSeconds(5));

        _viewModel.SelectedAudioOutputDevice.Should().Be(device);
        await _audioPlayer.Received(1).SetAudioOutputDeviceAsync(device.Id);
        await _settings.Received(1).SetAudioOutputDeviceIdAsync(device.Id);
    }

    [Fact]
    public async Task AudioOutput_SaveFailureKeepsLiveSelectionAndReportsFailure()
    {
        var device = new AudioOutputDeviceInfo("dac", "DAC");
        _audioPlayer.SetAudioOutputDeviceAsync(device.Id).Returns(true);
        _settings.SetAudioOutputDeviceIdAsync(device.Id).Returns(Task.FromException(new IOException("Storage unavailable")));

        await _viewModel.SelectAudioOutputDeviceCommand.ExecuteAsync(device);

        _viewModel.SelectedAudioOutputDevice.Should().Be(device);
        _viewModel.AudioOutputDeviceMessage.Should().NotBeEmpty();
        _viewModel.IsAudioOutputBusy.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LastFm_CancelIgnoresLateTokenOrSession(bool finalizing)
    {
        var token = new TaskCompletionSource<(string Token, string AuthUrl)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new TaskCompletionSource<(string Username, string SessionKey)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _lastFm.GetAuthenticationTokenAsync().Returns(token.Task);
        _settings.GetLastFmAuthTokenAsync().Returns("temporary");
        _lastFm.GetSessionAsync("temporary").Returns(session.Task);
        var authenticating = finalizing
            ? _viewModel.LastFmFinalizeAuthCommand.ExecuteAsync(null)
            : _viewModel.LastFmInitialAuthCommand.ExecuteAsync(null);

        await _viewModel.CancelLastFmAuthCommand.ExecuteAsync(null);
        token.SetResult(("late-token", "not a URI"));
        session.SetResult(("user", "late-session"));
        await authenticating.WaitAsync(TimeSpan.FromSeconds(5));

        _viewModel.IsLastFmConnected.Should().BeFalse();
        _viewModel.IsConnectingToLastFm.Should().BeFalse();
        await _settings.DidNotReceive().SaveLastFmAuthTokenAsync("late-token");
        await _settings.DidNotReceive().SaveLastFmCredentialsAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LastFm_RequestFailureResetsConnectionFlow(bool finalizing)
    {
        _settings.GetLastFmAuthTokenAsync().Returns("temporary");
        if (finalizing)
            _lastFm.GetSessionAsync("temporary").Returns(Task.FromException<(string Username, string SessionKey)?>(new IOException("Offline")));
        else
            _lastFm.GetAuthenticationTokenAsync().Returns(Task.FromException<(string Token, string AuthUrl)?>(new IOException("Offline")));
        _viewModel.IsConnectingToLastFm = true;

        await (finalizing ? _viewModel.LastFmFinalizeAuthCommand : _viewModel.LastFmInitialAuthCommand).ExecuteAsync(null);

        _viewModel.IsConnectingToLastFm.Should().BeFalse();
        await _settings.Received(1).SaveLastFmAuthTokenAsync(null);
        await _ui.Received(1).ShowMessageDialogAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task LastFm_FinalizePersistsTogglesOnce()
    {
        _settings.GetLastFmAuthTokenAsync().Returns("temporary");
        _lastFm.GetSessionAsync("temporary").Returns(("user", "session"));

        await _viewModel.LastFmFinalizeAuthCommand.ExecuteAsync(null);

        _viewModel.IsLastFmConnected.Should().BeTrue();
        _viewModel.IsLastFmScrobblingEnabled.Should().BeTrue();
        _viewModel.IsLastFmNowPlayingEnabled.Should().BeTrue();
        await _settings.Received(1).SetLastFmScrobblingEnabledAsync(true);
        await _settings.Received(1).SetLastFmNowPlayingEnabledAsync(true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListenBrainz_LateValidationCannotRestoreDisconnectedOrReplacedToken(bool disconnect)
    {
        var validation = new TaskCompletionSource<ValidateTokenResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _listenBrainz.ValidateTokenAsync("old", null).Returns(validation.Task);
        _ui.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>()).Returns(true);
        _viewModel.ListenBrainzUserToken = "old";
        var checking = _viewModel.TestListenBrainzConnectionCommand.ExecuteAsync(null);
        if (disconnect) await _viewModel.DisconnectListenBrainzCommand.ExecuteAsync(null);
        else _viewModel.ListenBrainzUserToken = "new";

        validation.SetResult(new ValidateTokenResult(true, "user", null));
        await checking.WaitAsync(TimeSpan.FromSeconds(5));

        _viewModel.ListenBrainzIsConnected.Should().BeFalse();
        await _settings.DidNotReceive().SaveListenBrainzUserTokenAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task MetadataRescan_RechecksOperationStateAfterConfirmation()
    {
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>()).Returns(confirmation.Task);
        var scan = _viewModel.RescanMetadataCommand.ExecuteAsync(null);
        _player.IsGlobalOperationInProgress = true;

        confirmation.SetResult(true);
        await scan.WaitAsync(TimeSpan.FromSeconds(5));

        await _library.DidNotReceive().ForceRescanMetadataAsync(Arg.Any<IProgress<ScanProgress>>(), Arg.Any<CancellationToken>());
        _player.IsGlobalOperationInProgress.Should().BeTrue();
    }

    [Fact]
    public async Task MetadataRescan_DisposalCancelsWithoutFailureOrCompletionDialog()
    {
        _ui.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>()).Returns(true);
        var scanning = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _library.ForceRescanMetadataAsync(Arg.Any<IProgress<ScanProgress>>(), Arg.Any<CancellationToken>()).Returns(scanning.Task);
        var scan = _viewModel.RescanMetadataCommand.ExecuteAsync(null);

        _viewModel.Dispose();
        scanning.SetResult(true);
        await scan.WaitAsync(TimeSpan.FromSeconds(5));

        _player.IsGlobalOperationInProgress.Should().BeFalse();
        _player.IsGlobalOperationCancellable.Should().BeFalse();
        await _ui.DidNotReceive().ShowMessageDialogAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task ReplayGain_UsesGlobalCancelAndReleasesOperationState()
    {
        _ffmpeg.IsFFmpegInstalledAsync().Returns(true);
        _ui.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>()).Returns(true);
        CancellationToken scanToken = default;
        _replayGain.ScanLibraryAsync(Arg.Any<IProgress<ScanProgress>>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            scanToken = call.Arg<CancellationToken>();
            return Task.Delay(Timeout.Infinite, scanToken);
        });
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlayerViewModel.IsGlobalOperationInProgress) && !_player.IsGlobalOperationInProgress)
                finished.TrySetResult();
        };

        _viewModel.IsVolumeNormalizationEnabled = true;
        _player.IsGlobalOperationCancellable.Should().BeTrue();
        _player.CancelGlobalOperationCommand.Execute(null);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));

        scanToken.IsCancellationRequested.Should().BeTrue();
        _player.IsGlobalOperationCancellable.Should().BeFalse();
    }

    [Fact]
    public async Task MetadataRescan_QueuedProgressCannotOverwriteFollowingOperation()
    {
        _ui.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>()).Returns(true);
        _library.ForceRescanMetadataAsync(Arg.Any<IProgress<ScanProgress>>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<IProgress<ScanProgress>>().Report(new ScanProgress { StatusText = "Old progress", Percentage = 90 });
            return true;
        });
        var context = new QueuedContext();
        var previous = SynchronizationContext.Current;
        Task scan;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            scan = _viewModel.RescanMetadataCommand.ExecuteAsync(null);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        await scan.WaitAsync(TimeSpan.FromSeconds(5));
        _player.GlobalOperationStatusMessage = "New operation";
        _player.GlobalOperationProgressValue = 5;

        context.RunAll();

        _player.GlobalOperationStatusMessage.Should().Be("New operation");
        _player.GlobalOperationProgressValue.Should().Be(5);
    }

    [Fact]
    public async Task Normalization_DisableSupersedesPendingEnableCheck()
    {
        var ffmpeg = new TaskCompletionSource<bool>();
        _ffmpeg.IsFFmpegInstalledAsync().Returns(ffmpeg.Task);
        _ui.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>()).Returns(true);
        var context = new QueuedContext();
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            _viewModel.IsVolumeNormalizationEnabled = true;
            _viewModel.IsVolumeNormalizationEnabled = false;
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }

        ffmpeg.SetResult(true);
        context.RunAll();

        _viewModel.IsVolumeNormalizationEnabled.Should().BeFalse();
        await _settings.Received(1).SetVolumeNormalizationEnabledAsync(false);
        await _settings.DidNotReceive().SetVolumeNormalizationEnabledAsync(true);
        await _replayGain.DidNotReceive().ScanLibraryAsync(Arg.Any<IProgress<ScanProgress>>(), Arg.Any<CancellationToken>());
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();
        public override void Post(SendOrPostCallback callback, object? state) => _callbacks.Enqueue((callback, state));
        public void RunAll()
        {
            while (_callbacks.TryDequeue(out var item)) item.Callback(item.State);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupToggle_ReadsActualStateEvenWhenRequestFails(bool fails)
    {
        var change = new TaskCompletionSource();
        _settings.SetAutoLaunchEnabledAsync(true).Returns(change.Task);
        _settings.GetAutoLaunchEnabledAsync().Returns(false);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.IsUpdatingAutoLaunch) && !_viewModel.IsUpdatingAutoLaunch)
                finished.TrySetResult();
        };
        _viewModel.IsAutoLaunchEnabled = true;
        _viewModel.IsUpdatingAutoLaunch.Should().BeTrue();

        if (fails) change.SetException(new InvalidOperationException("Startup request denied"));
        else change.SetResult();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _viewModel.IsAutoLaunchEnabled.Should().BeFalse();
        await _settings.Received(1).SetAutoLaunchEnabledAsync(true);
        await _settings.DidNotReceive().SetAutoLaunchEnabledAsync(false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedLanguageList_DoesNotSaveLanguageOrUpdateDisposedView(bool dispose)
    {
        var languages = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _appInfo.GetAvailableLanguagesAsync().Returns(languages.Task);
        _settings.GetLanguageAsync().Returns("ja");
        await _viewModel.LoadSettingsAsync();
        var originalSelection = _viewModel.SelectedLanguage;
        if (dispose) _viewModel.Dispose();
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.TryEnqueue(Arg.Any<Action>()).Returns(call =>
        {
            call.Arg<Action>()();
            dispatched.TrySetResult();
            return true;
        });

        languages.SetResult(new[] { "ja-JP", "JA-jp", "en-US" });
        await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));

        if (dispose) _viewModel.SelectedLanguage.Should().BeSameAs(originalSelection);
        else
        {
            _viewModel.SelectedLanguage!.Code.Should().Be("ja-JP");
            _viewModel.AvailableLanguages.Select(language => language.Code).Should().Equal("", "en-US", "ja-JP");
        }
        await _settings.DidNotReceive().SetLanguageAsync(Arg.Any<string>());
        await _ui.DidNotReceive().ShowMessageDialogAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task DisposeDuringLoad_DoesNotPopulateSettings()
    {
        var startup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _settings.GetAutoLaunchEnabledAsync().Returns(startup.Task);
        var first = _viewModel.LoadSettingsAsync();
        var second = _viewModel.LoadSettingsAsync();

        _viewModel.Dispose();
        startup.SetResult(true);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        _viewModel.AvailableLanguages.Should().BeEmpty();
        _viewModel.IsAutoLaunchEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task ResetPlayerDesign_CancelsOlderTintSave()
    {
        _viewModel.PlayerTintIntensity = .75;

        await _viewModel.ResetPlayerDesignSettingsCommand.ExecuteAsync(null);
        await AdvanceDebouncesAsync();

        await _settings.Received(1).SetPlayerTintIntensityAsync(Nagi.WinUI.Services.SettingsDefaults.DefaultPlayerTintIntensity);
        await _settings.DidNotReceive().SetPlayerTintIntensityAsync(.75);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetDuringLoad_DoesNotEnablePersistenceForInitialization(bool playerDesign)
    {
        var startup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _settings.GetAutoLaunchEnabledAsync().Returns(startup.Task);
        _appInfo.GetAvailableLanguagesAsync().Returns(Array.Empty<string>());
        var loading = _viewModel.LoadSettingsAsync();

        await (playerDesign ? _viewModel.ResetPlayerDesignSettingsCommand : _viewModel.ResetAccentColorCommand).ExecuteAsync(null);
        startup.SetResult(true);
        await loading.WaitAsync(TimeSpan.FromSeconds(5));

        _viewModel.IsAutoLaunchEnabled.Should().BeTrue();
        await _settings.DidNotReceive().SetAutoLaunchEnabledAsync(Arg.Any<bool>());
    }

    [Fact]
    public async Task EqualizerBand_InitialValueDoesNotCommit_ButLatestEditDoes()
    {
        using var band = new EqualizerBandViewModel(0, 100, 4, new ImmediateDispatcher(), _timeProvider);
        var commits = new List<float>();
        band.GainCommitted += (_, gain) => commits.Add(gain);

        await AdvanceDebouncesAsync();
        await band.PendingGainCommit.WaitAsync(TimeSpan.FromSeconds(5));

        commits.Should().BeEmpty();

        band.Gain = 5;
        band.Gain = 6;
        await AdvanceDebouncesAsync();
        await band.PendingGainCommit.WaitAsync(TimeSpan.FromSeconds(5));

        commits.Should().Equal(6);
    }

    [Fact]
    public async Task EqualizerPreset_CancelsOlderBandCommit()
    {
        var band = new EqualizerBandViewModel(0, 100, 0, new ImmediateDispatcher(), _timeProvider);
        _viewModel.EqualizerBands.Add(band);
        var commits = new List<float>();
        band.GainCommitted += (_, gain) => commits.Add(gain);
        band.Gain = 3;

        _playback.SetEqualizerGainsAsync(Arg.Any<IEnumerable<float>>()).Returns(call =>
        {
            _playback.CurrentEqualizerSettings.Returns(new EqualizerSettings { BandGains = call.Arg<IEnumerable<float>>().ToList() });
            _playback.EqualizerChanged += Raise.Event<Action>();
            return Task.CompletedTask;
        });

        _viewModel.SelectedEqualizerPreset = new EqualizerPreset("Preset", [7]);
        await AdvanceDebouncesAsync();

        band.Gain.Should().Be(7);
        commits.Should().BeEmpty();
    }

    [Fact]
    public async Task EqualizerReset_CancelsOlderPreampAndBandCommits()
    {
        var band = new EqualizerBandViewModel(0, 100, 0, new ImmediateDispatcher(), _timeProvider);
        _viewModel.EqualizerBands.Add(band);
        var commits = new List<float>();
        band.GainCommitted += (_, gain) => commits.Add(gain);
        band.Gain = 3;
        _viewModel.EqualizerPreamp = 2;

        await _viewModel.ResetEqualizerCommand.ExecuteAsync(null);
        await AdvanceDebouncesAsync();

        commits.Should().BeEmpty();
        await _playback.DidNotReceive().SetEqualizerPreampAsync(Arg.Any<float>());
        await _playback.Received(1).ResetEqualizerAsync();
    }

    [Fact]
    public async Task ResetAccentColor_CancelsPendingColorSave()
    {
        _viewModel.IsDynamicThemingEnabled = false;
        _theme.ClearReceivedCalls();
        _viewModel.AccentColor = Windows.UI.Color.FromArgb(255, 200, 10, 20);

        await _viewModel.ResetAccentColorCommand.ExecuteAsync(null);
        await AdvanceDebouncesAsync();

        await _settings.Received(1).SetAccentColorAsync(null);
        await _settings.DidNotReceive().SetAccentColorAsync(Arg.Is<Windows.UI.Color?>(color => color.HasValue));
        await _theme.Received(1).ApplyAccentColorAsync(null);
        await _theme.DidNotReceive().ApplyAccentColorAsync(Arg.Is<Windows.UI.Color?>(color => color.HasValue));
    }

    [Fact]
    public async Task ResetPlayerButtons_ReplacesLayoutOnceAndSavesWithoutPendingDuplicates()
    {
        var defaults = new List<PlayerButtonSetting>
        {
            new() { Id = "PlayPause", DisplayName = "Play", IconGlyph = "play", IsEnabled = true },
            new() { Id = "Next", DisplayName = "Next", IconGlyph = "next", IsEnabled = true }
        };
        _settings.GetDefaultPlayerButtonSettings().Returns(defaults);
        _viewModel.PlayerButtons.Add(new PlayerButtonSetting
            { Id = "Old", DisplayName = "Old", IconGlyph = "old", IsEnabled = true });
        var collectionChanges = 0;
        _viewModel.PlayerButtons.CollectionChanged += (_, _) => collectionChanges++;

        await _viewModel.ResetPlayerButtonsCommand.ExecuteAsync(null);
        await AdvanceDebouncesAsync();

        _viewModel.PlayerButtons.Should().Equal(defaults);
        collectionChanges.Should().Be(1);
        await _settings.Received(1).SetPlayerButtonSettingsAsync(Arg.Is<List<PlayerButtonSetting>>(
            buttons => buttons.SequenceEqual(defaults)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingSaves_CoalesceIndependently_AndCancelOnDisposal(bool dispose)
    {
        _viewModel.ArtistSplitCharacters = "first";
        _viewModel.ArtistSplitCharacters = "artist";
        _viewModel.GenreSplitCharacters = "first";
        _viewModel.GenreSplitCharacters = "genre";
        _viewModel.EqualizerPreamp = 1;
        _viewModel.EqualizerPreamp = 2;
        if (dispose) _viewModel.Dispose();

        await AdvanceDebouncesAsync();

        if (dispose)
        {
            await _settings.DidNotReceive().SetArtistSplitCharactersAsync(Arg.Any<string>());
            await _settings.DidNotReceive().SetGenreSplitCharactersAsync(Arg.Any<string>());
            await _playback.DidNotReceive().SetEqualizerPreampAsync(Arg.Any<float>());
        }
        else
        {
            await _settings.Received(1).SetArtistSplitCharactersAsync("artist");
            await _settings.Received(1).SetGenreSplitCharactersAsync("genre");
            await _playback.Received(1).SetEqualizerPreampAsync(2);
            await _settings.DidNotReceive().SetArtistSplitCharactersAsync("first");
            await _settings.DidNotReceive().SetGenreSplitCharactersAsync("first");
            await _playback.DidNotReceive().SetEqualizerPreampAsync(1);
        }
    }

    private async Task AdvanceDebouncesAsync()
    {
        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        await Task.WhenAll(_viewModel.PendingSettingsSaves,
            Task.WhenAll(_viewModel.EqualizerBands.Select(band => band.PendingGainCommit)))
            .WaitAsync(TimeSpan.FromSeconds(5));
    }
}

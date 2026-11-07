using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Settings;
using Nagi.Presentation.Models;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.Services;
using Nagi.Presentation.ViewModels;
using NSubstitute;
using Xunit;
using Nagi.Presentation.Resources;

namespace Nagi.Presentation.Tests;

public class SettingsViewModelTests : IDisposable
{
    private readonly ICredentialLockerService _credentials = Substitute.For<ICredentialLockerService>();
    private readonly SettingsService _settings;
    private readonly ISettingsService _resetSettings = Substitute.For<ISettingsService>();
    private readonly IMusicPlaybackService _playback = Substitute.For<IMusicPlaybackService>();
    private readonly IAudioPlayer _audioPlayer = Substitute.For<IAudioPlayer>();
    private readonly IAppInfoService _appInfo = Substitute.For<IAppInfoService>();
    private readonly IUIService _ui = Substitute.For<IUIService>();
    private readonly IApplicationLifecycle _lifecycle = Substitute.For<IApplicationLifecycle>();
    private readonly IAutoLaunchService _autoLaunch = Substitute.For<IAutoLaunchService>();
    private readonly IBackupRestoreService _backup = Substitute.For<IBackupRestoreService>();
    private readonly IDispatcherService _dispatcher = Substitute.For<IDispatcherService>();
    private readonly ILastFmAuthService _lastFm = Substitute.For<ILastFmAuthService>();
    private readonly IListenBrainzScrobblerService _listenBrainz = Substitute.For<IListenBrainzScrobblerService>();
    private readonly LibraryFakes _library = new LibraryFakes();
    private readonly IFFmpegService _ffmpeg = Substitute.For<IFFmpegService>();
    private readonly IReplayGainService _replayGain = Substitute.For<IReplayGainService>();
    private readonly GlobalOperationViewModel _operation = new();
    private readonly SettingsViewModel _viewModel;
    private readonly FakeTimeProvider _timeProvider = new();

    public SettingsViewModelTests()
    {
        _settings = TestSettings.Create(credentials: _credentials, autoLaunch: _autoLaunch);
        _credentials.SaveCredential(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _playback.AvailablePresets.Returns(Array.Empty<EqualizerPreset>());
        _dispatcher.HasThreadAccess.Returns(true);
        _dispatcher.TryEnqueue(Arg.Any<Action>()).Returns(call => { call.Arg<Action>()(); return true; });
        _dispatcher.EnqueueAsync(Arg.Any<Func<Task>>()).Returns(call => call.Arg<Func<Task>>()());
        _viewModel = new SettingsViewModel(_settings, _appInfo, _autoLaunch, _dispatcher,
            new AppearanceSettingsViewModel(_settings, _ui, _appInfo, _lifecycle, _dispatcher,
                NullLogger<AppearanceSettingsViewModel>.Instance, _timeProvider),
            new AudioSettingsViewModel(_playback, _audioPlayer, _settings, _ui, _ffmpeg, _replayGain, _operation, _dispatcher,
                NullLogger<AudioSettingsViewModel>.Instance, _timeProvider),
            new LibrarySettingsViewModel(_settings, _ui, _library.Scanner, Substitute.For<IMusicServerService>(), _operation, _dispatcher,
                NullLogger<LibrarySettingsViewModel>.Instance, _timeProvider),
            new ScrobblingSettingsViewModel(_settings, _ui, _lastFm, _listenBrainz, _dispatcher,
                NullLogger<ScrobblingSettingsViewModel>.Instance),
            new RomanizationPacksSettingsViewModel(Substitute.For<IRomanizationPackManager>(), _dispatcher,
                NullLogger<RomanizationPacksSettingsViewModel>.Instance),
            new DataManagementSettingsViewModel(_ui, Substitute.For<IPlaylistExportService>(),
                _backup, _lifecycle, _resetSettings, _library.Writer, _playback,
                NullLogger<DataManagementSettingsViewModel>.Instance),
            NullLogger<SettingsViewModel>.Instance, _timeProvider);
    }

    public void Dispose()
    {
        _viewModel.Dispose();
    }

    [Fact]
    public void SettingProperties_WriteTheStoreAndFollowChangesMadeElsewhere()
    {
        var changed = new List<string?>();
        _viewModel.Library.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        _viewModel.Library.ArtistSplitCharacters.Should().BeEmpty();

        _settings.Set(CoreSettings.ArtistSplitCharacters, ";");
        _viewModel.Library.GenreSplitCharacters = "/";

        _viewModel.Library.ArtistSplitCharacters.Should().Be(";");
        changed.Should().Contain(nameof(LibrarySettingsViewModel.ArtistSplitCharacters));
        _settings.Get(CoreSettings.GenreSplitCharacters).Should().Be("/");
    }

    [Fact]
    public async Task AudioOutput_MissingSavedDeviceDisplaysDefaultWithoutLosingPreference()
    {
        _audioPlayer.GetAudioOutputDevicesAsync().Returns(Array.Empty<AudioOutputDeviceInfo>());
        _settings.Set(CoreSettings.AudioOutputDeviceId, "disconnected-dac");

        await _viewModel.Audio.RefreshAudioOutputDevicesCommand.ExecuteAsync(null);

        _viewModel.Audio.SelectedAudioOutputDevice!.Id.Should().BeEmpty();
        _viewModel.Audio.AudioOutputDeviceMessage.Should().NotBeEmpty();
        _settings.Get(CoreSettings.AudioOutputDeviceId).Should().Be("disconnected-dac");
    }

    [Fact]
    public async Task AudioOutput_ReconnectedDeviceDoesNotMisrepresentVlcFallbackAsAnActiveSelection()
    {
        var device = new AudioOutputDeviceInfo("dac", "DAC");
        _settings.Set(CoreSettings.AudioOutputDeviceId, device.Id);
        _audioPlayer.AudioOutputDeviceId.Returns(string.Empty);
        _audioPlayer.GetAudioOutputDevicesAsync().Returns(new[] { device });
        _audioPlayer.SetAudioOutputDeviceAsync(device.Id).Returns(true);

        await _viewModel.Audio.RefreshAudioOutputDevicesCommand.ExecuteAsync(null);
        _viewModel.Audio.SelectedAudioOutputDevice!.Id.Should().BeEmpty();
        await _viewModel.Audio.SelectAudioOutputDeviceCommand.ExecuteAsync(device);

        await _audioPlayer.Received(1).SetAudioOutputDeviceAsync(device.Id);
    }

    [Fact]
    public async Task AudioOutput_RejectedSelectionRetainsPreviousDeviceAndPreference()
    {
        var previous = new AudioOutputDeviceInfo("speakers", "Speakers");
        _viewModel.Audio.SelectedAudioOutputDevice = previous;
        var selectionNotifications = 0;
        _viewModel.Audio.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AudioSettingsViewModel.SelectedAudioOutputDevice)) selectionNotifications++;
        };

        await _viewModel.Audio.SelectAudioOutputDeviceCommand.ExecuteAsync(new AudioOutputDeviceInfo("removed", "Removed"));

        _viewModel.Audio.SelectedAudioOutputDevice.Should().Be(previous);
        selectionNotifications.Should().BeGreaterThan(0);
        _viewModel.Audio.AudioOutputDeviceMessage.Should().NotBeEmpty();
        _viewModel.Audio.IsAudioOutputBusy.Should().BeFalse();
        _settings.Get(CoreSettings.AudioOutputDeviceId).Should().BeEmpty();
    }

    [Fact]
    public async Task AudioOutput_ChangedDeviceListKeepsSelectionInCurrentCollection()
    {
        _audioPlayer.AudioOutputDeviceId.Returns("dac");
        _audioPlayer.GetAudioOutputDevicesAsync().Returns(new[] { new AudioOutputDeviceInfo("dac", "DAC") });
        _settings.Set(CoreSettings.AudioOutputDeviceId, "dac");
        await _viewModel.Audio.RefreshAudioOutputDevicesCommand.ExecuteAsync(null);

        _audioPlayer.GetAudioOutputDevicesAsync().Returns(new[]
        {
            new AudioOutputDeviceInfo("dac", "DAC"), new AudioOutputDeviceInfo("speakers", "Speakers")
        });
        await _viewModel.Audio.RefreshAudioOutputDevicesCommand.ExecuteAsync(null);

        _viewModel.Audio.SelectedAudioOutputDevice.Should().BeSameAs(_viewModel.Audio.AudioOutputDevices.Single(d => d.Id == "dac"));
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
        _viewModel.Audio.AudioOutputDevices = new[] { defaultDevice, device };
        _viewModel.Audio.SelectedAudioOutputDevice = defaultDevice;

        var refreshing = _viewModel.Audio.RefreshAudioOutputDevicesCommand.ExecuteAsync(null);
        var selecting = _viewModel.Audio.SelectAudioOutputDeviceCommand.ExecuteAsync(device);
        enumeration.SetResult(new[] { device });
        await refreshing.WaitAsync(TimeSpan.FromSeconds(5));
        _viewModel.Audio.SelectedAudioOutputDevice.Id.Should().BeEmpty();
        selection.SetResult(true);
        await selecting.WaitAsync(TimeSpan.FromSeconds(5));

        _viewModel.Audio.SelectedAudioOutputDevice.Should().Be(device);
        await _audioPlayer.Received(1).SetAudioOutputDeviceAsync(device.Id);
        _settings.Get(CoreSettings.AudioOutputDeviceId).Should().Be(device.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LastFm_CancelIgnoresLateTokenOrSession(bool finalizing)
    {
        var token = new TaskCompletionSource<(string Token, string AuthUrl)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new TaskCompletionSource<(string Username, string SessionKey)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _lastFm.GetAuthenticationTokenAsync().Returns(token.Task);
        _settings.Set(CoreSettings.LastFmAuthToken, "temporary");
        _lastFm.GetSessionAsync("temporary").Returns(session.Task);
        var authenticating = finalizing
            ? _viewModel.Scrobbling.LastFmFinalizeAuthCommand.ExecuteAsync(null)
            : _viewModel.Scrobbling.LastFmInitialAuthCommand.ExecuteAsync(null);

        _viewModel.Scrobbling.CancelLastFmAuthCommand.Execute(null);
        token.SetResult(("late-token", "not a URI"));
        session.SetResult(("user", "late-session"));
        await authenticating.WaitAsync(TimeSpan.FromSeconds(5));

        _viewModel.Scrobbling.IsLastFmConnected.Should().BeFalse();
        _viewModel.Scrobbling.IsConnectingToLastFm.Should().BeFalse();
        _settings.Get(CoreSettings.LastFmAuthToken).Should().BeNull();
        _credentials.DidNotReceive().SaveCredential(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LastFm_RequestFailureResetsConnectionFlow(bool finalizing)
    {
        _settings.Set(CoreSettings.LastFmAuthToken, "temporary");
        if (finalizing)
            _lastFm.GetSessionAsync("temporary").Returns(Task.FromException<(string Username, string SessionKey)?>(new IOException("Offline")));
        else
            _lastFm.GetAuthenticationTokenAsync().Returns(Task.FromException<(string Token, string AuthUrl)?>(new IOException("Offline")));
        _viewModel.Scrobbling.IsConnectingToLastFm = true;

        await (finalizing ? _viewModel.Scrobbling.LastFmFinalizeAuthCommand : _viewModel.Scrobbling.LastFmInitialAuthCommand).ExecuteAsync(null);

        _viewModel.Scrobbling.IsConnectingToLastFm.Should().BeFalse();
        _settings.Get(CoreSettings.LastFmAuthToken).Should().BeNull();
        await _ui.Received(1).ShowMessageDialogAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LastFm_InitialAuthOpensTheAuthPageAndKeepsTheTokenOnlyIfItOpened(bool opened)
    {
        _lastFm.GetAuthenticationTokenAsync().Returns(("token", "https://www.last.fm/api/auth?token=token"));
        _ui.OpenUriAsync(Arg.Any<Uri>()).Returns(opened);

        await _viewModel.Scrobbling.LastFmInitialAuthCommand.ExecuteAsync(null);

        await _ui.Received(1).OpenUriAsync(new Uri("https://www.last.fm/api/auth?token=token"));
        _viewModel.Scrobbling.IsConnectingToLastFm.Should().Be(opened);
        _settings.Get(CoreSettings.LastFmAuthToken).Should().Be(opened ? "token" : null);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RestoreFromBackup_RestartsOnlyWhenTheRestoreRequiresIt(bool requiresRestart)
    {
        _ui.PickSingleFileAsync(Arg.Any<IEnumerable<string>>()).Returns("backup.zip");
        _backup.ValidateBackupAsync("backup.zip").Returns(true);
        _ui.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<bool>()).Returns(true);
        _backup.RestoreFromBackupAsync("backup.zip").Returns(new RestoreResult(true, 1, requiresRestart));

        await _viewModel.DataManagement.RestoreFromBackupCommand.ExecuteAsync(null);

        _lifecycle.Received(requiresRestart ? 1 : 0).Restart();
    }

    [Fact]
    public async Task ResetApplicationData_ClearsEverythingBeforeRestarting()
    {
        _ui.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<bool>()).Returns(true);

        await _viewModel.DataManagement.ResetApplicationDataCommand.ExecuteAsync(null);

        Received.InOrder(() =>
        {
            _resetSettings.ResetToDefaultsAsync();
            _library.Writer.ClearAllLibraryDataAsync();
            _playback.ClearQueueAsync();
            _lifecycle.Restart();
        });
    }

    [Fact]
    public async Task ResetApplicationData_WhenClearingFails_ReportsItAndDoesNotRestart()
    {
        _ui.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<bool>()).Returns(true);
        _library.Writer.ClearAllLibraryDataAsync().Returns(Task.FromException(new IOException("locked")));

        await _viewModel.DataManagement.ResetApplicationDataCommand.ExecuteAsync(null);

        _lifecycle.DidNotReceive().Restart();
        await _ui.Received(1).ShowMessageDialogAsync(Arg.Any<string>(), Arg.Is<string>(m => m.Contains("locked")));
    }

    [Fact]
    public async Task LastFm_FinalizeSavesTheSessionAndTurnsSubmissionsOn()
    {
        _settings.Set(CoreSettings.LastFmAuthToken, "temporary");
        _lastFm.GetSessionAsync("temporary").Returns(("user", "session"));

        await _viewModel.Scrobbling.LastFmFinalizeAuthCommand.ExecuteAsync(null);

        _viewModel.Scrobbling.IsLastFmConnected.Should().BeTrue();
        _viewModel.Scrobbling.IsLastFmScrobblingEnabled.Should().BeTrue();
        _viewModel.Scrobbling.IsLastFmNowPlayingEnabled.Should().BeTrue();
        _settings.Get(CoreSettings.LastFmAuthToken).Should().BeNull();
        _credentials.Received(1).SaveCredential(Arg.Any<string>(), "user", "session");
    }

    [Fact]
    public async Task LastFm_FinalizeReportsASessionTheCredentialStoreRefused()
    {
        _credentials.SaveCredential(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        _settings.Set(CoreSettings.LastFmAuthToken, "temporary");
        _lastFm.GetSessionAsync("temporary").Returns(("user", "session"));

        await _viewModel.Scrobbling.LastFmFinalizeAuthCommand.ExecuteAsync(null);

        _viewModel.Scrobbling.IsLastFmConnected.Should().BeFalse();
        _viewModel.Scrobbling.IsLastFmScrobblingEnabled.Should().BeFalse();
        await _ui.Received(1).ShowMessageDialogAsync(Arg.Any<string>(), Strings.MusicServer_Error_SignInNotSaved);
    }

    [Fact]
    public async Task ListenBrainz_ValidTokenTheCredentialStoreRefusedIsNotConnected()
    {
        _credentials.SaveCredential(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        _listenBrainz.ValidateTokenAsync("token", null).Returns(new ValidateTokenResult(true, "user", null));
        _viewModel.Scrobbling.ListenBrainzUserToken = "token";

        await _viewModel.Scrobbling.TestListenBrainzConnectionCommand.ExecuteAsync(null);

        _viewModel.Scrobbling.ListenBrainzIsConnected.Should().BeFalse();
        _viewModel.Scrobbling.ListenBrainzConnectionStatus.Should().Contain(Strings.MusicServer_Error_SignInNotSaved);
        _settings.Get(CoreSettings.ListenBrainzEnabledSinceUtc).Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListenBrainz_LateValidationCannotRestoreDisconnectedOrReplacedToken(bool disconnect)
    {
        var validation = new TaskCompletionSource<ValidateTokenResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _listenBrainz.ValidateTokenAsync("old", null).Returns(validation.Task);
        _ui.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>()).Returns(true);
        _viewModel.Scrobbling.ListenBrainzUserToken = "old";
        var checking = _viewModel.Scrobbling.TestListenBrainzConnectionCommand.ExecuteAsync(null);
        if (disconnect) await _viewModel.Scrobbling.DisconnectListenBrainzCommand.ExecuteAsync(null);
        else _viewModel.Scrobbling.ListenBrainzUserToken = "new";

        validation.SetResult(new ValidateTokenResult(true, "user", null));
        await checking.WaitAsync(TimeSpan.FromSeconds(5));

        _viewModel.Scrobbling.ListenBrainzIsConnected.Should().BeFalse();
        _credentials.DidNotReceive().SaveCredential(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task MetadataRescan_RechecksOperationStateAfterConfirmation()
    {
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>()).Returns(confirmation.Task);
        var scan = _viewModel.Library.RescanMetadataCommand.ExecuteAsync(null);
        _operation.IsInProgress = true;

        confirmation.SetResult(true);
        await scan.WaitAsync(TimeSpan.FromSeconds(5));

        await _library.Scanner.DidNotReceive().ForceRescanMetadataAsync(Arg.Any<IProgress<ScanProgress>>(), Arg.Any<CancellationToken>());
        _operation.IsInProgress.Should().BeTrue();
    }

    [Fact]
    public async Task MetadataRescan_DisposalCancelsWithoutFailureOrCompletionDialog()
    {
        _ui.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>()).Returns(true);
        var scanning = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _library.Scanner.ForceRescanMetadataAsync(Arg.Any<IProgress<ScanProgress>>(), Arg.Any<CancellationToken>()).Returns(scanning.Task);
        var scan = _viewModel.Library.RescanMetadataCommand.ExecuteAsync(null);

        _viewModel.Dispose();
        scanning.SetResult(true);
        await scan.WaitAsync(TimeSpan.FromSeconds(5));

        _operation.IsInProgress.Should().BeFalse();
        _operation.IsCancellable.Should().BeFalse();
        await _ui.DidNotReceive().ShowMessageDialogAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task ReplayGain_UsesGlobalCancelAndReleasesOperationState()
    {
        _ffmpeg.IsFFmpegInstalledAsync().Returns(true);
        _ui.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>()).Returns(true);
        CancellationToken scanToken = default;
        _replayGain.ScanLibraryAsync(Arg.Any<IProgress<ScanProgress>>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            scanToken = call.Arg<CancellationToken>();
            return Task.Delay(Timeout.Infinite, scanToken);
        });
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _operation.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GlobalOperationViewModel.IsInProgress) && !_operation.IsInProgress)
                finished.TrySetResult();
        };

        _viewModel.Audio.IsVolumeNormalizationEnabled = true;
        _operation.IsCancellable.Should().BeTrue();
        _operation.CancelCommand.Execute(null);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));

        scanToken.IsCancellationRequested.Should().BeTrue();
        _operation.IsCancellable.Should().BeFalse();
    }

    [Fact]
    public async Task MetadataRescan_QueuedProgressCannotOverwriteFollowingOperation()
    {
        _ui.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>()).Returns(true);
        _library.Scanner.ForceRescanMetadataAsync(Arg.Any<IProgress<ScanProgress>>(), Arg.Any<CancellationToken>()).Returns(call =>
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
            scan = _viewModel.Library.RescanMetadataCommand.ExecuteAsync(null);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        await scan.WaitAsync(TimeSpan.FromSeconds(5));
        _operation.StatusMessage = "New operation";
        _operation.ProgressValue = 5;

        context.RunAll();

        _operation.StatusMessage.Should().Be("New operation");
        _operation.ProgressValue.Should().Be(5);
    }

    [Fact]
    public async Task Normalization_DisableSupersedesPendingEnableCheck()
    {
        var ffmpeg = new TaskCompletionSource<bool>();
        _ffmpeg.IsFFmpegInstalledAsync().Returns(ffmpeg.Task);
        _ui.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>()).Returns(true);
        var context = new QueuedContext();
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            _viewModel.Audio.IsVolumeNormalizationEnabled = true;
            _viewModel.Audio.IsVolumeNormalizationEnabled = false;
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }

        ffmpeg.SetResult(true);
        context.RunAll();

        _viewModel.Audio.IsVolumeNormalizationEnabled.Should().BeFalse();
        _settings.Get(CoreSettings.VolumeNormalization).Should().BeFalse();
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
        _autoLaunch.SetEnabledAsync(true).Returns(change.Task);
        _autoLaunch.IsEnabledAsync().Returns(false);
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
        await _autoLaunch.Received(1).SetEnabledAsync(true);
        await _autoLaunch.DidNotReceive().SetEnabledAsync(false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedLanguageList_DoesNotSaveLanguageOrUpdateDisposedView(bool dispose)
    {
        var languages = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _appInfo.GetAvailableLanguagesAsync().Returns(languages.Task);
        _settings.Set(AppSettings.Language, "ja");
        await _viewModel.LoadSettingsAsync();
        var originalSelection = _viewModel.Appearance.SelectedLanguage;
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

        if (dispose) _viewModel.Appearance.SelectedLanguage.Should().BeSameAs(originalSelection);
        else
        {
            _viewModel.Appearance.SelectedLanguage!.Code.Should().Be("ja-JP");
            _viewModel.Appearance.AvailableLanguages.Select(language => language.Code).Should().Equal("", "en-US", "ja-JP");
        }
        _settings.Get(AppSettings.Language).Should().Be("ja");
        await _ui.DidNotReceive().ShowMessageDialogAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task DisposeDuringLoad_DoesNotApplyAutoLaunchState()
    {
        var startup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _autoLaunch.IsEnabledAsync().Returns(startup.Task);
        var first = _viewModel.LoadSettingsAsync();
        var second = _viewModel.LoadSettingsAsync();

        _viewModel.Dispose();
        startup.SetResult(true);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        _viewModel.IsAutoLaunchEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Load_PinsMusicBrainzFirstWithoutSaving()
    {
        _settings.Set(CoreSettings.MetadataProviders, new List<ServiceProviderSetting>
        {
            new() { Id = ServiceProviderIds.TheAudioDb, Category = ServiceCategory.Metadata, Order = 0 },
            new() { Id = ServiceProviderIds.MusicBrainz, Category = ServiceCategory.Metadata, Order = 1 }
        });
        var changed = new List<Setting>();
        _settings.SettingChanged += changed.Add;

        await _viewModel.LoadSettingsAsync();
        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        await _viewModel.PendingSettingsSaves.WaitAsync(TimeSpan.FromSeconds(5));

        _viewModel.Library.MetadataProviders.Select(p => p.Id).Should()
            .StartWith(new[] { ServiceProviderIds.MusicBrainz, ServiceProviderIds.TheAudioDb });
        changed.Should().BeEmpty();
    }

    [Fact]
    public async Task DraggingAProviderAboveMusicBrainz_KeepsMusicBrainzFirst()
    {
        await _viewModel.LoadSettingsAsync();
        var providers = _viewModel.Library.MetadataProviders;
        providers[0].Id.Should().Be(ServiceProviderIds.MusicBrainz);
        var dragged = providers[^1];

        // A list view reorders by removing the item and inserting it again.
        providers.Remove(dragged);
        providers.Insert(0, dragged);
        await AdvanceDebouncesAsync();

        providers.Select(p => p.Id).Should().StartWith(new[] { ServiceProviderIds.MusicBrainz, dragged.Id });
        _settings.Get(CoreSettings.MetadataProviders).Select(p => p.Id).Should()
            .Equal(providers.Select(p => p.Id));
    }

    [Fact]
    public async Task ResetPlayerDesign_CancelsOlderTintSave()
    {
        _viewModel.Appearance.PlayerTintIntensity = .75;

        _viewModel.Appearance.ResetPlayerDesignSettingsCommand.Execute(null);
        await AdvanceDebouncesAsync();

        _viewModel.Appearance.PlayerTintIntensity.Should().Be(AppSettings.PlayerTintIntensity.Default);
        _settings.Get(AppSettings.PlayerTintIntensity).Should().Be(AppSettings.PlayerTintIntensity.Default);
    }

    [Fact]
    public void ResetSongListColumns_SavesTheDefaultsOnceRatherThanEachToggle()
    {
        var trackNumber = _viewModel.Appearance.SongListColumnSettings.Single(s => s.Column == SongListColumns.TrackNumber);
        trackNumber.IsEnabled = true;
        var changed = new List<Setting>();
        _settings.SettingChanged += changed.Add;

        _viewModel.Appearance.ResetSongListColumnsCommand.Execute(null);

        trackNumber.IsEnabled.Should().BeFalse();
        changed.Should().Equal(AppSettings.SongListColumns);
        _settings.Get(AppSettings.SongListColumns).Should().Be(AppSettings.SongListColumns.Default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetDuringLoad_DoesNotEnablePersistenceForInitialization(bool playerDesign)
    {
        var startup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _autoLaunch.IsEnabledAsync().Returns(startup.Task);
        _appInfo.GetAvailableLanguagesAsync().Returns(Array.Empty<string>());
        var loading = _viewModel.LoadSettingsAsync();

        if (playerDesign) _viewModel.Appearance.ResetPlayerDesignSettingsCommand.Execute(null);
        else _viewModel.Appearance.ResetAccentColorCommand.Execute(null);
        startup.SetResult(true);
        await loading.WaitAsync(TimeSpan.FromSeconds(5));

        _viewModel.IsAutoLaunchEnabled.Should().BeTrue();
        await _autoLaunch.DidNotReceive().SetEnabledAsync(Arg.Any<bool>());
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
        _viewModel.Audio.EqualizerBands.Add(band);
        var commits = new List<float>();
        band.GainCommitted += (_, gain) => commits.Add(gain);
        band.Gain = 3;

        _playback.SetEqualizerGainsAsync(Arg.Any<IEnumerable<float>>()).Returns(call =>
        {
            _playback.CurrentEqualizerSettings.Returns(new EqualizerSettings { BandGains = call.Arg<IEnumerable<float>>().ToList() });
            _playback.EqualizerChanged += Raise.Event<Action>();
            return Task.CompletedTask;
        });

        _viewModel.Audio.SelectedEqualizerPreset = new EqualizerPreset("Preset", [7]);
        await AdvanceDebouncesAsync();

        band.Gain.Should().Be(7);
        commits.Should().BeEmpty();
    }

    [Fact]
    public async Task EqualizerReset_CancelsOlderPreampAndBandCommits()
    {
        var band = new EqualizerBandViewModel(0, 100, 0, new ImmediateDispatcher(), _timeProvider);
        _viewModel.Audio.EqualizerBands.Add(band);
        var commits = new List<float>();
        band.GainCommitted += (_, gain) => commits.Add(gain);
        band.Gain = 3;
        _viewModel.Audio.EqualizerPreamp = 2;

        await _viewModel.Audio.ResetEqualizerCommand.ExecuteAsync(null);
        await AdvanceDebouncesAsync();

        commits.Should().BeEmpty();
        await _playback.DidNotReceive().SetEqualizerPreampAsync(Arg.Any<float>());
        await _playback.Received(1).ResetEqualizerAsync();
    }

    [Fact]
    public async Task ResetAccentColor_CancelsPendingColorSave()
    {
        _viewModel.Appearance.AccentColor = ArgbColor.FromRgb(200, 10, 20);

        _viewModel.Appearance.ResetAccentColorCommand.Execute(null);
        await AdvanceDebouncesAsync();

        _viewModel.Appearance.AccentColor.Should().Be(ThemePalette.BrandAccentColor);
        _settings.Get(AppSettings.AccentColor).Should().BeNull();
    }

    [Fact]
    public async Task ResetPlayerButtons_ReplacesLayoutOnceAndSavesWithoutPendingDuplicates()
    {
        var defaults = AppSettings.PlayerButtons.Default.Select(button => (button.Id, button.IsEnabled)).ToList();
        _viewModel.PlayerButtons.Add(new PlayerButtonSetting { Id = "Shuffle", DisplayName = "Shuffle", IsEnabled = false });
        var collectionChanges = 0;
        _viewModel.PlayerButtons.CollectionChanged += (_, _) => collectionChanges++;

        _viewModel.ResetPlayerButtonsCommand.Execute(null);
        await AdvanceDebouncesAsync();

        _viewModel.PlayerButtons.Select(button => (button.Id, button.IsEnabled)).Should().Equal(defaults);
        collectionChanges.Should().Be(1);
        _settings.Get(AppSettings.PlayerButtons).Select(button => (button.Id, button.IsEnabled)).Should().Equal(defaults);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingSaves_CoalesceIndependently_AndCancelOnDisposal(bool dispose)
    {
        var tintChanges = 0;
        _settings.SettingChanged += setting => { if (setting == AppSettings.PlayerTintIntensity) tintChanges++; };
        _viewModel.Appearance.PlayerTintIntensity = .2;
        _viewModel.Appearance.PlayerTintIntensity = .3;
        _viewModel.Audio.EqualizerPreamp = 1;
        _viewModel.Audio.EqualizerPreamp = 2;
        if (dispose) _viewModel.Dispose();

        await AdvanceDebouncesAsync();

        if (dispose)
        {
            tintChanges.Should().Be(0);
            await _playback.DidNotReceive().SetEqualizerPreampAsync(Arg.Any<float>());
        }
        else
        {
            tintChanges.Should().Be(1);
            _settings.Get(AppSettings.PlayerTintIntensity).Should().Be(.3);
            await _playback.Received(1).SetEqualizerPreampAsync(2);
            await _playback.DidNotReceive().SetEqualizerPreampAsync(1);
        }
    }

    private async Task AdvanceDebouncesAsync()
    {
        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        await Task.WhenAll(_viewModel.PendingSettingsSaves,
            Task.WhenAll(_viewModel.Audio.EqualizerBands.Select(band => band.PendingGainCommit)))
            .WaitAsync(TimeSpan.FromSeconds(5));
    }
}

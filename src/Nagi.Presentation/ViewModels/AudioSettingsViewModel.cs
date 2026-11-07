using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Helpers;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Core.Settings;

namespace Nagi.Presentation.ViewModels;

/// <summary>
///     Settings section for audio playback: volume normalization, exclusive mode, fades, the equalizer
///     and the audio output device.
/// </summary>
public partial class AudioSettingsViewModel : SettingsSectionViewModel
{
    internal const int EqualizerDebounceDelayMilliseconds = 300;

    private readonly IMusicPlaybackService _playbackService;
    private readonly IAudioPlayer _audioPlayer;
    private readonly IUIService _uiService;
    private readonly IFFmpegService _ffmpegService;
    private readonly IReplayGainService _replayGainService;
    private readonly GlobalOperationViewModel _globalOperation;
    private readonly TimeProvider _timeProvider;
    private readonly Debouncer _preampDebouncer;
    private bool _isRefreshingAudioOutputDevices;
    private bool _isSynchronizingAudioOutputSelection;
    private bool _isApplyingPreset;
    private int _volumeNormalizationVersion;
    private CancellationTokenSource? _replayGainScanCts;

    public AudioSettingsViewModel(
        IMusicPlaybackService playbackService,
        IAudioPlayer audioPlayer,
        ISettingsService settingsService,
        IUIService uiService,
        IFFmpegService ffmpegService,
        IReplayGainService replayGainService,
        GlobalOperationViewModel globalOperation,
        IDispatcherService dispatcherService,
        ILogger<AudioSettingsViewModel> logger,
        TimeProvider? timeProvider = null)
        : base(settingsService, dispatcherService, logger)
    {
        _playbackService = playbackService;
        _audioPlayer = audioPlayer;
        _uiService = uiService;
        _ffmpegService = ffmpegService;
        _replayGainService = replayGainService;
        _globalOperation = globalOperation;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _preampDebouncer = new Debouncer(TimeSpan.FromMilliseconds(EqualizerDebounceDelayMilliseconds), _timeProvider);

        _playbackService.EqualizerChanged += OnPlaybackService_EqualizerChanged;
        AvailableEqualizerPresets = _playbackService.AvailablePresets;
    }

    public bool IsVolumeNormalizationEnabled
    {
        get => Get(CoreSettings.VolumeNormalization);
        set
        {
            // Turning it off also cancels an enable still waiting on FFmpeg or the user, which hasn't saved yet.
            if (!value) DisableVolumeNormalization();
            else if (!IsVolumeNormalizationEnabled) _ = EnableVolumeNormalizationAsync();
        }
    }

    public bool IsWasapiExclusiveModeEnabled
    {
        get => Get(CoreSettings.WasapiExclusiveMode);
        set => Set(CoreSettings.WasapiExclusiveMode, value);
    }

    public bool IsFadeOnPlayPauseEnabled
    {
        get => Get(CoreSettings.FadeOnPlayPause);
        set => Set(CoreSettings.FadeOnPlayPause, value);
    }

    public double FadeInDurationMs
    {
        get => Get(CoreSettings.FadeInDurationMs);
        set => Set(CoreSettings.FadeInDurationMs, (int)value);
    }

    public double FadeOutDurationMs
    {
        get => Get(CoreSettings.FadeOutDurationMs);
        set => Set(CoreSettings.FadeOutDurationMs, (int)value);
    }

    [ObservableProperty] public partial IReadOnlyList<AudioOutputDeviceInfo> AudioOutputDevices { get; set; } = Array.Empty<AudioOutputDeviceInfo>();
    [ObservableProperty] public partial AudioOutputDeviceInfo? SelectedAudioOutputDevice { get; set; }
    [ObservableProperty] public partial string AudioOutputDeviceMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsAudioOutputBusy { get; set; }

    [ObservableProperty] public partial float EqualizerPreamp { get; set; }
    [ObservableProperty] public partial EqualizerPreset? SelectedEqualizerPreset { get; set; }
    public IReadOnlyList<EqualizerPreset> AvailableEqualizerPresets { get; }
    public ObservableCollection<EqualizerBandViewModel> EqualizerBands { get; } = new();

    internal Task PendingSettingsSaves => _preampDebouncer.PendingInvocation;

    /// <summary>
    ///     Rebuilds the equalizer bands from the playback service without writing anything back.
    /// </summary>
    public void Load() => ApplyWithoutSaving(LoadEqualizerState);

    protected override void OnDisposing()
    {
        _replayGainScanCts?.Cancel();
        _playbackService.EqualizerChanged -= OnPlaybackService_EqualizerChanged;
        foreach (var bandVm in EqualizerBands)
        {
            bandVm.GainChanged -= OnBandGainChanged;
            bandVm.GainCommitted -= OnBandGainCommitted;
            bandVm.Dispose();
        }
        _preampDebouncer.Dispose();
    }

    [RelayCommand]
    private async Task RefreshAudioOutputDevicesAsync()
    {
        if (_isDisposed || IsAudioOutputBusy || _isRefreshingAudioOutputDevices) return;
        _isRefreshingAudioOutputDevices = true;
        try
        {
            var devices = await _audioPlayer.GetAudioOutputDevicesAsync();
            var savedId = _settingsService.Get(CoreSettings.AudioOutputDeviceId);
            // A user selection can start while enumeration is awaiting VLC. Let
            // that operation own the displayed selection until it finishes.
            if (_isDisposed || IsAudioOutputBusy) return;
            _isSynchronizingAudioOutputSelection = true;
            var defaultDevice = new AudioOutputDeviceInfo(string.Empty, Strings.SettingsPage_AudioOutput_Default);
            var refreshedDevices = new[] { defaultDevice }.Concat(devices).ToArray();
            if (!AudioOutputDevices.SequenceEqual(refreshedDevices))
            {
                // Clear the old item before replacing the collection; record equality
                // must not leave ComboBox pointing at an item from the previous list.
                SelectedAudioOutputDevice = null;
                AudioOutputDevices = refreshedDevices;
            }
            // VLC can fall back after a disconnect. Display the live route, keeping
            // the saved preference for the next launch until the user changes it.
            var currentId = _audioPlayer.AudioOutputDeviceId;
            SelectedAudioOutputDevice = AudioOutputDevices.FirstOrDefault(d => d.Id == currentId) ?? AudioOutputDevices[0];
            AudioOutputDeviceMessage = !string.IsNullOrEmpty(savedId) && !devices.Any(d => d.Id == savedId)
                ? Strings.SettingsPage_AudioOutput_Unavailable : string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not refresh audio outputs.");
            AudioOutputDeviceMessage = Strings.SettingsPage_AudioOutput_Error;
        }
        finally
        {
            _isSynchronizingAudioOutputSelection = false;
            _isRefreshingAudioOutputDevices = false;
        }
    }

    [RelayCommand]
    private async Task SelectAudioOutputDeviceAsync(AudioOutputDeviceInfo? device)
    {
        if (_isDisposed || IsAudioOutputBusy || _isSynchronizingAudioOutputSelection || device is null ||
            (device.Id == SelectedAudioOutputDevice?.Id && AudioOutputDeviceMessage.Length == 0)) return;
        IsAudioOutputBusy = true;
        try
        {
            var accepted = await _audioPlayer.SetAudioOutputDeviceAsync(device.Id);
            if (_isDisposed) return;
            if (!accepted)
            {
                AudioOutputDeviceMessage = Strings.SettingsPage_AudioOutput_Error;
                // Restore the displayed selection when VLC rejects a request.
                OnPropertyChanged(nameof(SelectedAudioOutputDevice));
                return;
            }
            SelectedAudioOutputDevice = device;
            _settingsService.Set(CoreSettings.AudioOutputDeviceId, device.Id);
            AudioOutputDeviceMessage = string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not change or save audio output {DeviceId}.", device.Id);
            AudioOutputDeviceMessage = Strings.SettingsPage_AudioOutput_Error;
            OnPropertyChanged(nameof(SelectedAudioOutputDevice));
        }
        finally { IsAudioOutputBusy = false; }
    }

    [RelayCommand]
    private async Task ResetEqualizerAsync()
    {
        _preampDebouncer.Cancel();
        foreach (var band in EqualizerBands) band.CancelPendingGain();
        try
        {
            await _playbackService.ResetEqualizerAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset the equalizer");
        }
    }

    private void LoadEqualizerState()
    {
        _logger.LogDebug("Loading Equalizer State...");
        var eqSettings = _playbackService.CurrentEqualizerSettings;
        if (eqSettings == null)
        {
            _logger.LogWarning("Equalizer settings are null.");
            return;
        }

        _logger.LogDebug("Loaded Settings - Preamp: {Preamp}, Bands: {Bands}", eqSettings.Preamp, string.Join(", ", eqSettings.BandGains));

        EqualizerPreamp = eqSettings.Preamp;

        foreach (var bandVm in EqualizerBands)
        {
            bandVm.GainChanged -= OnBandGainChanged;
            bandVm.GainCommitted -= OnBandGainCommitted;
            bandVm.Dispose();
        }
        EqualizerBands.Clear();

        foreach (var bandInfo in _playbackService.EqualizerBands)
        {
            var gain = eqSettings.BandGains.ElementAtOrDefault((int)bandInfo.Index);
            var bandVm = new EqualizerBandViewModel(bandInfo.Index, bandInfo.Frequency, gain, _dispatcherService, _timeProvider);
            bandVm.GainChanged += OnBandGainChanged;
            bandVm.GainCommitted += OnBandGainCommitted;
            EqualizerBands.Add(bandVm);
        }

        CheckIfCurrentSettingsMatchPreset();
    }

    private async void OnBandGainCommitted(EqualizerBandViewModel sender, float gain)
    {
        if (_isInitializing) return;
        try
        {
            await _playbackService.SetEqualizerBandAsync(sender.Index, gain);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply equalizer band {BandIndex}", sender.Index);
        }
    }

    private void OnBandGainChanged(EqualizerBandViewModel sender) => CheckIfCurrentSettingsMatchPreset();

    private void CheckIfCurrentSettingsMatchPreset()
    {
        if (_isApplyingPreset) return;

        var currentGains = EqualizerBands.Select(b => b.Gain).ToArray();

        var matchingPreset = _playbackService.GetMatchingPreset(currentGains);

        if (SelectedEqualizerPreset != matchingPreset)
        {
            _isApplyingPreset = true; // Prevent re-trigger loop
            SelectedEqualizerPreset = matchingPreset;
            _isApplyingPreset = false;
        }
    }

    async partial void OnSelectedEqualizerPresetChanged(EqualizerPreset? value)
    {
        if (_isInitializing || _isDisposed || value == null || _isApplyingPreset) return;

        foreach (var band in EqualizerBands) band.CancelPendingGain();
        try
        {
            await _playbackService.SetEqualizerGainsAsync(value.Gains);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying equalizer preset {PresetName}", value.Name);
        }
    }

    private void OnPlaybackService_EqualizerChanged()
    {
        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed) return;

            ApplyWithoutSaving(() =>
            {
                var eqSettings = _playbackService.CurrentEqualizerSettings;
                if (eqSettings != null)
                {
                    EqualizerPreamp = eqSettings.Preamp;
                    foreach (var band in EqualizerBands)
                        band.SetGainFromService(eqSettings.BandGains.ElementAtOrDefault((int)band.Index));
                }
                CheckIfCurrentSettingsMatchPreset();
            });
        });
    }

    partial void OnEqualizerPreampChanged(float value)
    {
        if (_isInitializing) return;
        QueueSettingsSave(_preampDebouncer, _ => _playbackService.SetEqualizerPreampAsync(value), nameof(EqualizerPreamp));
    }

    private void DisableVolumeNormalization()
    {
        ++_volumeNormalizationVersion;
        _replayGainScanCts?.Cancel();
        Set(CoreSettings.VolumeNormalization, false, nameof(IsVolumeNormalizationEnabled));
    }

    // Normalization needs FFmpeg and a library scan, so it stays off until the user confirms. When they back out,
    // the toggle goes back to the saved value.
    private async Task EnableVolumeNormalizationAsync()
    {
        if (_isDisposed) return;
        var version = ++_volumeNormalizationVersion;
        bool IsCurrent() => !_isDisposed && version == _volumeNormalizationVersion;

        try
        {
            var ffmpegInstalled = await _ffmpegService.IsFFmpegInstalledAsync();
            if (!IsCurrent()) return;
            if (!ffmpegInstalled)
            {
                ffmpegInstalled = await _uiService.ShowFFmpegSetupDialogAsync(
                    () => _ffmpegService.IsFFmpegInstalledAsync(forceRecheck: true));
                if (!IsCurrent()) return;
                if (!ffmpegInstalled)
                {
                    OnPropertyChanged(nameof(IsVolumeNormalizationEnabled));
                    return;
                }
            }

            var confirmed = await _uiService.ShowConfirmationDialogAsync(
                Strings.Settings_Dialog_VolumeNorm_Title,
                Strings.Settings_Dialog_VolumeNorm_Content,
                Strings.Settings_Dialog_VolumeNorm_PrimaryButton,
                Strings.Generic_Cancel);
            if (!IsCurrent()) return;
            if (!confirmed)
            {
                OnPropertyChanged(nameof(IsVolumeNormalizationEnabled));
                return;
            }

            if (Set(CoreSettings.VolumeNormalization, true, nameof(IsVolumeNormalizationEnabled)) && IsCurrent())
                await ScanLibraryForReplayGainAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error turning volume normalization on");
            if (!IsCurrent()) return;
            Set(CoreSettings.VolumeNormalization, false, nameof(IsVolumeNormalizationEnabled));
            OnPropertyChanged(nameof(IsVolumeNormalizationEnabled));
        }
    }

    private async Task ScanLibraryForReplayGainAsync()
    {
        if (_isDisposed || _globalOperation.IsInProgress) return;

        using var scanCts = new CancellationTokenSource();
        _replayGainScanCts = scanCts;
        var cancellationToken = scanCts.Token;

        _globalOperation.IsInProgress = true;
        _globalOperation.StatusMessage = Strings.Settings_Status_VolumeNorm_Preparing;
        _globalOperation.IsIndeterminate = true;
        _globalOperation.SetCancellation(scanCts.Cancel);

        string? error = null;
        try
        {
            await _replayGainService.ScanLibraryAsync(_globalOperation.CreateProgress(cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            error = ResourceFormatter.Format(Strings.Settings_Status_VolumeNorm_Error_Format, ex.Message);
            _logger.LogError(ex, "Failed to scan library for ReplayGain");
        }
        finally
        {
            scanCts.Cancel();
            _replayGainScanCts = null;
            _globalOperation.SetCancellation(null);
            _globalOperation.IsInProgress = false;
            _globalOperation.IsIndeterminate = false;
        }

        if (error != null && !_isDisposed) await _uiService.ShowMessageDialogAsync(Strings.Generic_Error, error);
    }
}

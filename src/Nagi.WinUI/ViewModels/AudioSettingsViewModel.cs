using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Resources;
using Nagi.WinUI.Services;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     Settings section for the equalizer and the audio output device.
/// </summary>
public partial class AudioSettingsViewModel : SettingsSectionViewModel
{
    internal const int EqualizerDebounceDelayMilliseconds = 300;

    private readonly IMusicPlaybackService _playbackService;
    private readonly IAudioPlayer _audioPlayer;
    private readonly IUISettingsService _settingsService;
    private readonly TimeProvider _timeProvider;
    private readonly Debouncer _preampDebouncer;
    private bool _isRefreshingAudioOutputDevices;
    private bool _isSynchronizingAudioOutputSelection;
    private bool _isApplyingPreset;

    public AudioSettingsViewModel(
        IMusicPlaybackService playbackService,
        IAudioPlayer audioPlayer,
        IUISettingsService settingsService,
        IDispatcherService dispatcherService,
        ILogger<AudioSettingsViewModel> logger,
        TimeProvider? timeProvider = null)
        : base(dispatcherService, logger)
    {
        _playbackService = playbackService ?? throw new ArgumentNullException(nameof(playbackService));
        _audioPlayer = audioPlayer ?? throw new ArgumentNullException(nameof(audioPlayer));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _preampDebouncer = new Debouncer(TimeSpan.FromMilliseconds(EqualizerDebounceDelayMilliseconds), _timeProvider);

        _playbackService.EqualizerChanged += OnPlaybackService_EqualizerChanged;
        AvailableEqualizerPresets = _playbackService.AvailablePresets;

        ApplyWithoutSaving(() => EqualizerPreamp = SettingsDefaults.EqualizerPreamp);
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
            var savedId = await _settingsService.GetAudioOutputDeviceIdAsync();
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
            await _settingsService.SetAudioOutputDeviceIdAsync(device.Id);
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
        await _playbackService.ResetEqualizerAsync();
        // Resetting will result in all 0s, which matches "None".
        SelectedEqualizerPreset = AvailableEqualizerPresets.FirstOrDefault(p => p.Name == Nagi.Core.Resources.Strings.EqPreset_None);
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
}

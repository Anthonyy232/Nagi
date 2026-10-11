using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using System.Runtime.CompilerServices;
using Windows.System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Nagi.WinUI.Resources;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Models;
using Nagi.WinUI.Models;
using Nagi.WinUI.Navigation;
using Nagi.WinUI.Services;
using Nagi.WinUI.Services.Abstractions;
using Microsoft.Windows.AppLifecycle;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     ViewModel for a single band in the audio equalizer.
/// </summary>
public partial class EqualizerBandViewModel : ObservableObject, IDisposable
{
    private const int KiloHertzThreshold = 1000;

    private readonly Debouncer _debouncer;
    private readonly IDispatcherService _dispatcherService;
    private bool _isExternalUpdate;

    public EqualizerBandViewModel(uint index, float frequency, float initialGain, IDispatcherService dispatcherService, TimeProvider? timeProvider = null)
    {
        _dispatcherService = dispatcherService;
        _debouncer = new Debouncer(TimeSpan.FromMilliseconds(SettingsViewModel.EqualizerDebounceDelayMilliseconds), timeProvider);
        Index = index;
        FrequencyLabel = frequency < KiloHertzThreshold ? $"{frequency:F0}" : $"{frequency / KiloHertzThreshold:F0}K";
        SetGainFromService(initialGain);
    }

    public uint Index { get; }
    public string FrequencyLabel { get; }

    [ObservableProperty] public partial float Gain { get; set; }

    public event Action<EqualizerBandViewModel>? GainChanged;
    public event Action<EqualizerBandViewModel, float>? GainCommitted;

    public void SetGainFromService(float gain)
    {
        _isExternalUpdate = true;
        try { Gain = gain; }
        finally { _isExternalUpdate = false; }
    }

    internal Task PendingGainCommit => _debouncer.PendingInvocation;

    public void CancelPendingGain() => _debouncer.Cancel();

    public void Dispose() => _debouncer.Dispose();

    partial void OnGainChanged(float value)
    {
        if (_isExternalUpdate) return;

        GainChanged?.Invoke(this);
        _debouncer.Trigger(token => _dispatcherService.EnqueueAsync(() =>
        {
            if (!token.IsCancellationRequested) GainCommitted?.Invoke(this, value);
            return Task.CompletedTask;
        }));
    }
}




/// <summary>
///     ViewModel for the Settings page, providing properties and commands to manage application settings.
/// </summary>
public partial class SettingsViewModel : ObservableObject, IDisposable
{
    internal const int EqualizerDebounceDelayMilliseconds = 300;
    private const int VisualSettingDebounceMs = 200;

    private readonly IAppInfoService _appInfoService;
    private readonly IApplicationLifecycle _applicationLifecycle;
    private readonly ILastFmAuthService _lastFmAuthService;
    private readonly IListenBrainzScrobblerService _listenBrainzScrobblerService;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly IMusicPlaybackService _playbackService;
    private readonly IAudioPlayer _audioPlayer;
    private readonly IUISettingsService _settingsService;
    private readonly IThemeService _themeService;
    private readonly IUIService _uiService;
    private readonly IPlaylistExportService _playlistExportService;
    private readonly ILibraryScanner _libraryScanner;
    private readonly PlayerViewModel _playerViewModel;
    private readonly IReplayGainService _replayGainService;
    private readonly IFFmpegService _ffmpegService;
    private readonly IBackupRestoreService _backupRestoreService;
    private readonly IDispatcherService _dispatcherService;
    private readonly IRomanizationPackManager _romanizationPackManager;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    private const int SettingsSaveDebounceMs = 300;
    private readonly TimeProvider _timeProvider;

    private bool _isDisposed;
    private bool _isRefreshingAudioOutputDevices;
    private bool _isSynchronizingAudioOutputSelection;
    private bool _isInitializing;
    private bool _isLoaded;
    private int _lastFmAuthVersion;
    private int _listenBrainzConnectionVersion;
    private int _volumeNormalizationVersion;
    private readonly Debouncer _preampDebouncer;
    private CancellationTokenSource? _replayGainScanCts;
    private CancellationTokenSource? _metadataRescanCts;
    private readonly Debouncer _playerButtonSaveDebouncer;
    private readonly Debouncer _navigationItemSaveDebouncer;
    private readonly Debouncer _lyricsProviderSaveDebouncer;
    private readonly Debouncer _metadataProviderSaveDebouncer;
    private readonly Debouncer _artistSplitSaveDebouncer;
    private readonly Debouncer _genreSplitSaveDebouncer;
    private readonly Debouncer _playerTintSaveDebouncer;
    private readonly Debouncer _accentColorSaveDebouncer;
    private CancellationTokenSource? _romanizationPackCts;

    public SettingsViewModel(
        IUISettingsService settingsService,
        IUIService uiService,
        IThemeService themeService,
        IApplicationLifecycle applicationLifecycle,
        IAppInfoService appInfoService,
        ILastFmAuthService lastFmAuthService,
        IListenBrainzScrobblerService listenBrainzScrobblerService,
        IMusicPlaybackService playbackService,
        IAudioPlayer audioPlayer,
        IPlaylistExportService playlistExportService,
        ILibraryScanner libraryScanner,
        PlayerViewModel playerViewModel,
        IReplayGainService replayGainService,
        IFFmpegService ffmpegService,
        IBackupRestoreService backupRestoreService,
        IDispatcherService dispatcherService,
        IRomanizationPackManager romanizationPackManager,
        ILogger<SettingsViewModel> logger,
        TimeProvider? timeProvider = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _uiService = uiService ?? throw new ArgumentNullException(nameof(uiService));
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        _applicationLifecycle = applicationLifecycle ?? throw new ArgumentNullException(nameof(applicationLifecycle));
        _appInfoService = appInfoService ?? throw new ArgumentNullException(nameof(appInfoService));
        _lastFmAuthService = lastFmAuthService ?? throw new ArgumentNullException(nameof(lastFmAuthService));
        _listenBrainzScrobblerService = listenBrainzScrobblerService ?? throw new ArgumentNullException(nameof(listenBrainzScrobblerService));
        _playbackService = playbackService ?? throw new ArgumentNullException(nameof(playbackService));
        _audioPlayer = audioPlayer ?? throw new ArgumentNullException(nameof(audioPlayer));
        _playlistExportService = playlistExportService ?? throw new ArgumentNullException(nameof(playlistExportService));
        _libraryScanner = libraryScanner ?? throw new ArgumentNullException(nameof(libraryScanner));
        _playerViewModel = playerViewModel ?? throw new ArgumentNullException(nameof(playerViewModel));
        _playerViewModel.PropertyChanged += OnPlayerViewModelPropertyChanged;
        _replayGainService = replayGainService ?? throw new ArgumentNullException(nameof(replayGainService));
        _ffmpegService = ffmpegService ?? throw new ArgumentNullException(nameof(ffmpegService));
        _backupRestoreService = backupRestoreService ?? throw new ArgumentNullException(nameof(backupRestoreService));
        _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));
        _romanizationPackManager = romanizationPackManager ?? throw new ArgumentNullException(nameof(romanizationPackManager));
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _preampDebouncer = new Debouncer(TimeSpan.FromMilliseconds(EqualizerDebounceDelayMilliseconds), _timeProvider);
        _playerButtonSaveDebouncer = new Debouncer(TimeSpan.FromMilliseconds(SettingsSaveDebounceMs), _timeProvider);
        _navigationItemSaveDebouncer = new Debouncer(TimeSpan.FromMilliseconds(SettingsSaveDebounceMs), _timeProvider);
        _lyricsProviderSaveDebouncer = new Debouncer(TimeSpan.FromMilliseconds(SettingsSaveDebounceMs), _timeProvider);
        _metadataProviderSaveDebouncer = new Debouncer(TimeSpan.FromMilliseconds(SettingsSaveDebounceMs), _timeProvider);
        _artistSplitSaveDebouncer = new Debouncer(TimeSpan.FromMilliseconds(SettingsSaveDebounceMs), _timeProvider);
        _genreSplitSaveDebouncer = new Debouncer(TimeSpan.FromMilliseconds(SettingsSaveDebounceMs), _timeProvider);
        _playerTintSaveDebouncer = new Debouncer(TimeSpan.FromMilliseconds(VisualSettingDebounceMs), _timeProvider);
        _accentColorSaveDebouncer = new Debouncer(TimeSpan.FromMilliseconds(VisualSettingDebounceMs), _timeProvider);

        NavigationItems.CollectionChanged += OnNavigationItemsCollectionChanged;
        PlayerButtons.CollectionChanged += OnPlayerButtonsCollectionChanged;
        LyricsProviders.CollectionChanged += OnLyricsProvidersCollectionChanged;
        MetadataProviders.CollectionChanged += OnMetadataProvidersCollectionChanged;
        _playbackService.EqualizerChanged += OnPlaybackService_EqualizerChanged;


        AvailableEqualizerPresets = _playbackService.AvailablePresets;

        _isInitializing = true;

        SelectedTheme = SettingsDefaults.Theme;
        SelectedBackdropMaterial = SettingsDefaults.DefaultBackdropMaterial;
        IsDynamicThemingEnabled = SettingsDefaults.DynamicThemingEnabled;
        IsPlayerAnimationEnabled = SettingsDefaults.PlayerAnimationEnabled;
        IsRestorePlaybackStateEnabled = SettingsDefaults.RestorePlaybackStateEnabled;
        IsAutoLaunchEnabled = SettingsDefaults.AutoLaunchEnabled;
        IsStartMinimizedEnabled = SettingsDefaults.StartMinimizedEnabled;
        IsHideToTrayEnabled = SettingsDefaults.HideToTrayEnabled;
        IsMinimizeToMiniPlayerEnabled = SettingsDefaults.MinimizeToMiniPlayerEnabled;
        IsShowCoverArtInTrayFlyoutEnabled = SettingsDefaults.ShowCoverArtInTrayFlyoutEnabled;
        IsFetchOnlineMetadataEnabled = SettingsDefaults.FetchOnlineMetadataEnabled;
        IsIgnoreLeadingArticlesOnSortEnabled = SettingsDefaults.IgnoreLeadingArticlesOnSortEnabled;
        IsFetchOnlineLyricsEnabled = SettingsDefaults.FetchOnlineLyricsEnabled;
        IsLyricsRomanizationEnabled = SettingsDefaults.LyricsRomanizationEnabled;
        IsDiscordRichPresenceEnabled = SettingsDefaults.DiscordRichPresenceEnabled;
        IsRememberWindowSizeEnabled = SettingsDefaults.RememberWindowSizeEnabled;
        IsRememberWindowPositionEnabled = SettingsDefaults.RememberWindowPositionEnabled;
        IsRememberPaneStateEnabled = SettingsDefaults.RememberPaneStateEnabled;
        IsVolumeNormalizationEnabled = SettingsDefaults.VolumeNormalizationEnabled;
        IsWasapiExclusiveModeEnabled = SettingsDefaults.WasapiExclusiveModeEnabled;
        IsFadeOnPlayPauseEnabled = SettingsDefaults.FadeOnPlayPauseEnabled;
        EqualizerPreamp = SettingsDefaults.EqualizerPreamp;

        _isInitializing = false;
    }




    [ObservableProperty] public partial ElementTheme SelectedTheme { get; set; }
    [ObservableProperty] public partial BackdropMaterial SelectedBackdropMaterial { get; set; }
    [ObservableProperty] public partial bool IsDynamicThemingEnabled { get; set; }
    [ObservableProperty] public partial bool IsPlayerAnimationEnabled { get; set; }
    [ObservableProperty] public partial bool IsRestorePlaybackStateEnabled { get; set; }
    [ObservableProperty] public partial bool IsAutoLaunchEnabled { get; set; }
    [ObservableProperty] public partial bool IsUpdatingAutoLaunch { get; set; }
    [ObservableProperty] public partial bool IsStartMinimizedEnabled { get; set; }
    [ObservableProperty] public partial bool IsHideToTrayEnabled { get; set; }
    [ObservableProperty] public partial bool IsMinimizeToMiniPlayerEnabled { get; set; }
    [ObservableProperty] public partial bool IsShowCoverArtInTrayFlyoutEnabled { get; set; }
    [ObservableProperty] public partial bool IsFetchOnlineMetadataEnabled { get; set; }
    [ObservableProperty] public partial bool IsIgnoreLeadingArticlesOnSortEnabled { get; set; }
    [ObservableProperty] public partial bool IsFetchOnlineLyricsEnabled { get; set; }
    [ObservableProperty] public partial bool IsLyricsRomanizationEnabled { get; set; }
    [ObservableProperty] public partial bool IsLoadingRomanizationPacks { get; set; }
    [ObservableProperty] public partial string? RomanizationPacksStatus { get; set; }
    [ObservableProperty] public partial bool IsDiscordRichPresenceEnabled { get; set; }
    [ObservableProperty] public partial bool IsRememberWindowSizeEnabled { get; set; }
    [ObservableProperty] public partial bool IsRememberWindowPositionEnabled { get; set; }
    [ObservableProperty] public partial bool IsRememberPaneStateEnabled { get; set; }
    [ObservableProperty] public partial bool IsVolumeNormalizationEnabled { get; set; }
    [ObservableProperty] public partial bool IsWasapiExclusiveModeEnabled { get; set; }

    [ObservableProperty] public partial IReadOnlyList<AudioOutputDeviceInfo> AudioOutputDevices { get; set; } = Array.Empty<AudioOutputDeviceInfo>();
    [ObservableProperty] public partial AudioOutputDeviceInfo? SelectedAudioOutputDevice { get; set; }
    [ObservableProperty] public partial string AudioOutputDeviceMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsAudioOutputBusy { get; set; }

    [ObservableProperty] public partial bool IsFadeOnPlayPauseEnabled { get; set; }
    [ObservableProperty] public partial double FadeInDurationMs { get; set; }
    [ObservableProperty] public partial double FadeOutDurationMs { get; set; }
    [ObservableProperty] public partial float EqualizerPreamp { get; set; }
    [ObservableProperty] public partial Windows.UI.Color AccentColor { get; set; }
    [ObservableProperty] public partial string ArtistSplitCharacters { get; set; } = string.Empty;
    [ObservableProperty] public partial string GenreSplitCharacters { get; set; } = string.Empty;
    partial void OnArtistSplitCharactersChanged(string value)
    {
        if (_isInitializing) return;
        QueueSettingsSave(_artistSplitSaveDebouncer, _ => _settingsService.SetArtistSplitCharactersAsync(value), nameof(ArtistSplitCharacters));
    }

    partial void OnGenreSplitCharactersChanged(string value)
    {
        if (_isInitializing) return;
        QueueSettingsSave(_genreSplitSaveDebouncer, _ => _settingsService.SetGenreSplitCharactersAsync(value), nameof(GenreSplitCharacters));
    }

    async partial void OnSelectedLanguageChanged(LanguageModel? value)
    {
        if (_isInitializing || _isDisposed || value == null) return;

        try
        {
            var currentLanguage = await _settingsService.GetLanguageAsync();
            if (string.Equals(value.Code, currentLanguage, StringComparison.OrdinalIgnoreCase)) return;

            await _settingsService.SetLanguageAsync(value.Code);

            var title = Strings.SettingsPage_RestartRequired_Title;
            var message = Strings.SettingsPage_RestartRequired_Message;

            await _uiService.ShowMessageDialogAsync(title, message);
            AppInstance.Restart("");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set language to {LanguageCode}", value.Code);
        }
    }

    [ObservableProperty] public partial EqualizerPreset? SelectedEqualizerPreset { get; set; }
    public IReadOnlyList<EqualizerPreset> AvailableEqualizerPresets { get; }

    private bool _isApplyingPreset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLastFmNotConnected))]
    public partial bool IsLastFmConnected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLastFmInitialAuthEnabled))]
    public partial bool IsConnectingToLastFm { get; set; }

    [ObservableProperty]
    public partial string? LastFmUsername { get; set; }
    [ObservableProperty] public partial bool IsLastFmScrobblingEnabled { get; set; }
    [ObservableProperty] public partial bool IsLastFmNowPlayingEnabled { get; set; }

    // ListenBrainz bindable state
    [ObservableProperty] public partial string ListenBrainzUserToken { get; set; } = string.Empty;
    [ObservableProperty] public partial bool ListenBrainzScrobblingEnabled { get; set; }
    [ObservableProperty] public partial bool ListenBrainzNowPlayingEnabled { get; set; }
    [ObservableProperty] public partial string ListenBrainzServerUrl { get; set; } = string.Empty;
    [ObservableProperty] public partial string? ListenBrainzConnectionStatus { get; set; }
    [ObservableProperty] public partial bool ListenBrainzIsConnected { get; set; }

    public ObservableCollection<EqualizerBandViewModel> EqualizerBands { get; } = new();
    public ObservableRangeCollection<PlayerButtonSetting> PlayerButtons { get; } = new();
    public bool IsLastFmNotConnected => !IsLastFmConnected;
    public bool IsLastFmInitialAuthEnabled => !IsConnectingToLastFm;
    public ObservableRangeCollection<NavigationItemSetting> NavigationItems { get; } = new();
    public ObservableRangeCollection<ServiceProviderSettingViewModel> LyricsProviders { get; } = new();
    public ObservableRangeCollection<ServiceProviderSettingViewModel> MetadataProviders { get; } = new();
    public ObservableRangeCollection<RomanizationPackViewModel> RomanizationPacks { get; } = new();


    public IReadOnlyList<ElementTheme> AvailableThemes { get; } = Enum.GetValues<ElementTheme>();
    public IReadOnlyList<BackdropMaterial> AvailableBackdropMaterials { get; } = Enum.GetValues<BackdropMaterial>();
    public IReadOnlyList<PlayerBackgroundMaterial> AvailablePlayerBackgroundMaterials { get; } = Enum.GetValues<PlayerBackgroundMaterial>();

    [ObservableProperty] public partial PlayerBackgroundMaterial SelectedPlayerBackgroundMaterial { get; set; }
    [ObservableProperty] public partial double PlayerTintIntensity { get; set; }

    partial void OnSelectedPlayerBackgroundMaterialChanged(PlayerBackgroundMaterial value) =>
        _ = SaveSettingAsync(() => _settingsService.SetPlayerBackgroundMaterialAsync(value));

    partial void OnPlayerTintIntensityChanged(double value)
    {
        if (_isInitializing) return;
        QueueSettingsSave(_playerTintSaveDebouncer, _ => _settingsService.SetPlayerTintIntensityAsync(value), nameof(PlayerTintIntensity));
    }

    public ObservableRangeCollection<LanguageModel> AvailableLanguages { get; } = new();

    [ObservableProperty] public partial LanguageModel? SelectedLanguage { get; set; }

    public string ApplicationVersion => _appInfoService.GetAppVersion();

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        NavigationItems.CollectionChanged -= OnNavigationItemsCollectionChanged;
        PlayerButtons.CollectionChanged -= OnPlayerButtonsCollectionChanged;
        LyricsProviders.CollectionChanged -= OnLyricsProvidersCollectionChanged;
        MetadataProviders.CollectionChanged -= OnMetadataProvidersCollectionChanged;
        _playbackService.EqualizerChanged -= OnPlaybackService_EqualizerChanged;

        foreach (var item in NavigationItems) item.PropertyChanged -= OnNavigationItemPropertyChanged;

        foreach (var item in PlayerButtons) item.PropertyChanged -= OnPlayerButtonPropertyChanged;

        foreach (var item in LyricsProviders) item.PropertyChanged -= OnLyricsProviderPropertyChanged;

        foreach (var item in MetadataProviders) item.PropertyChanged -= OnMetadataProviderPropertyChanged;

        foreach (var bandVm in EqualizerBands)
        {
            bandVm.GainChanged -= OnBandGainChanged;
            bandVm.GainCommitted -= OnBandGainCommitted;
            bandVm.Dispose();
        }
        _preampDebouncer.Dispose();
        _replayGainScanCts?.Cancel();
        _metadataRescanCts?.Cancel();
        _playerButtonSaveDebouncer.Dispose();
        _navigationItemSaveDebouncer.Dispose();
        _lyricsProviderSaveDebouncer.Dispose();
        _playerViewModel.PropertyChanged -= OnPlayerViewModelPropertyChanged;
        _metadataProviderSaveDebouncer.Dispose();
        _artistSplitSaveDebouncer.Dispose();
        _genreSplitSaveDebouncer.Dispose();
        _playerTintSaveDebouncer.Dispose();
        _accentColorSaveDebouncer.Dispose();
        _romanizationPackCts?.Cancel();
        _romanizationPackCts?.Dispose();

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
    public async Task LoadSettingsAsync()
    {
        if (_isDisposed) return;

        // If already loaded, don't reload everything.
        // This ViewModel is a singleton, so we only need to load once per app session.
        if (_isLoaded) return;

        await _loadLock.WaitAsync();
        try
        {
            if (_isDisposed || _isLoaded) return;

            _isInitializing = true;

            foreach (var item in NavigationItems) item.PropertyChanged -= OnNavigationItemPropertyChanged;
            NavigationItems.Clear();

            foreach (var item in PlayerButtons) item.PropertyChanged -= OnPlayerButtonPropertyChanged;
            PlayerButtons.Clear();

            foreach (var item in LyricsProviders) item.PropertyChanged -= OnLyricsProviderPropertyChanged;
            LyricsProviders.Clear();

            foreach (var item in MetadataProviders) item.PropertyChanged -= OnMetadataProviderPropertyChanged;
            MetadataProviders.Clear();
            RomanizationPacks.Clear();

            var navItemsTask = _settingsService.GetNavigationItemsAsync();
            var playerButtonsTask = _settingsService.GetPlayerButtonSettingsAsync();
            var themeTask = _settingsService.GetThemeAsync();
            var backdropTask = _settingsService.GetBackdropMaterialAsync();
            var dynamicThemingTask = _settingsService.GetDynamicThemingAsync();
            var playerAnimationTask = _settingsService.GetPlayerAnimationEnabledAsync();
            var restorePlaybackTask = _settingsService.GetRestorePlaybackStateEnabledAsync();
            var autoLaunchTask = _settingsService.GetAutoLaunchEnabledAsync();
            var startMinimizedTask = _settingsService.GetStartMinimizedEnabledAsync();
            var hideToTrayTask = _settingsService.GetHideToTrayEnabledAsync();
            var miniPlayerTask = _settingsService.GetMinimizeToMiniPlayerEnabledAsync();
            var trayFlyoutTask = _settingsService.GetShowCoverArtInTrayFlyoutAsync();
            var onlineMetadataTask = _settingsService.GetFetchOnlineMetadataEnabledAsync();
            var ignoreArticlesTask = _settingsService.GetIgnoreLeadingArticlesOnSortEnabledAsync();
            var onlineLyricsTask = _settingsService.GetFetchOnlineLyricsEnabledAsync();
            var lyricsRomanizationTask = _settingsService.GetLyricsRomanizationEnabledAsync();
            var discordRpcTask = _settingsService.GetDiscordRichPresenceEnabledAsync();
            var rememberWindowTask = _settingsService.GetRememberWindowSizeEnabledAsync();
            var rememberPositionTask = _settingsService.GetRememberWindowPositionEnabledAsync();
            var rememberPaneTask = _settingsService.GetRememberPaneStateEnabledAsync();
            var volumeNormTask = _settingsService.GetVolumeNormalizationEnabledAsync();
            var wasapiExclusiveTask = _settingsService.GetWasapiExclusiveModeEnabledAsync();
            var fadeTask = _settingsService.GetFadeOnPlayPauseEnabledAsync();
            var fadeInTask = _settingsService.GetFadeInDurationMsAsync();
            var fadeOutTask = _settingsService.GetFadeOutDurationMsAsync();
            var lastFmCredsTask = _settingsService.GetLastFmCredentialsAsync();
            var lastFmAuthTokenTask = _settingsService.GetLastFmAuthTokenAsync();
            var scrobblingTask = _settingsService.GetLastFmScrobblingEnabledAsync();
            var nowPlayingTask = _settingsService.GetLastFmNowPlayingEnabledAsync();
            var lbTokenTask = _settingsService.GetListenBrainzUserTokenAsync();
            var lbScrobblingTask = _settingsService.GetListenBrainzScrobblingEnabledAsync();
            var lbNowPlayingTask = _settingsService.GetListenBrainzNowPlayingEnabledAsync();
            var lbServerUrlTask = _settingsService.GetListenBrainzServerUrlAsync();

            var accentColorTask = _settingsService.GetAccentColorAsync();
            var artistSplitTask = _settingsService.GetArtistSplitCharactersAsync();
            var genreSplitTask = _settingsService.GetGenreSplitCharactersAsync();
            var languageTask = _settingsService.GetLanguageAsync();

            var lyricsProvidersTask = _settingsService.GetServiceProvidersAsync(ServiceCategory.Lyrics);
            var metadataProvidersTask = _settingsService.GetServiceProvidersAsync(ServiceCategory.Metadata);

            var playerMaterialTask = _settingsService.GetPlayerBackgroundMaterialAsync();
            var playerTintTask = _settingsService.GetPlayerTintIntensityAsync();

            await Task.WhenAll(
                navItemsTask, playerButtonsTask, themeTask, backdropTask, dynamicThemingTask,
                playerAnimationTask, restorePlaybackTask, autoLaunchTask, startMinimizedTask,
                hideToTrayTask, miniPlayerTask, trayFlyoutTask, onlineMetadataTask,
                onlineLyricsTask, lyricsRomanizationTask, discordRpcTask, rememberWindowTask,
                rememberPositionTask, rememberPaneTask, volumeNormTask, wasapiExclusiveTask, fadeTask, fadeInTask, fadeOutTask, lastFmCredsTask, lastFmAuthTokenTask,
                scrobblingTask, nowPlayingTask, accentColorTask, artistSplitTask, genreSplitTask, languageTask, lyricsProvidersTask, metadataProvidersTask,
                playerMaterialTask, playerTintTask,
                lbTokenTask, lbScrobblingTask, lbNowPlayingTask, lbServerUrlTask,
                ignoreArticlesTask);

            if (_isDisposed) return;

            foreach (var item in navItemsTask.Result)
            {
                item.PropertyChanged += OnNavigationItemPropertyChanged;
            }
            NavigationItems.ReplaceRange(navItemsTask.Result);

            foreach (var button in playerButtonsTask.Result)
            {
                button.PropertyChanged += OnPlayerButtonPropertyChanged;
            }
            PlayerButtons.ReplaceRange(playerButtonsTask.Result);

            SelectedTheme = themeTask.Result;
            SelectedBackdropMaterial = backdropTask.Result;
            IsDynamicThemingEnabled = dynamicThemingTask.Result;
            IsPlayerAnimationEnabled = playerAnimationTask.Result;
            IsRestorePlaybackStateEnabled = restorePlaybackTask.Result;
            IsAutoLaunchEnabled = autoLaunchTask.Result;
            IsStartMinimizedEnabled = startMinimizedTask.Result;
            IsHideToTrayEnabled = hideToTrayTask.Result;
            IsMinimizeToMiniPlayerEnabled = miniPlayerTask.Result;
            IsShowCoverArtInTrayFlyoutEnabled = trayFlyoutTask.Result;
            IsFetchOnlineMetadataEnabled = onlineMetadataTask.Result;
            IsIgnoreLeadingArticlesOnSortEnabled = ignoreArticlesTask.Result;
            IsFetchOnlineLyricsEnabled = onlineLyricsTask.Result;
            IsLyricsRomanizationEnabled = lyricsRomanizationTask.Result;
            IsDiscordRichPresenceEnabled = discordRpcTask.Result;
            IsRememberWindowSizeEnabled = rememberWindowTask.Result;
            IsRememberWindowPositionEnabled = rememberPositionTask.Result;
            IsRememberPaneStateEnabled = rememberPaneTask.Result;
            IsVolumeNormalizationEnabled = volumeNormTask.Result;
            IsWasapiExclusiveModeEnabled = wasapiExclusiveTask.Result;
            IsFadeOnPlayPauseEnabled = fadeTask.Result;
            FadeInDurationMs = fadeInTask.Result;
            FadeOutDurationMs = fadeOutTask.Result;

            SelectedPlayerBackgroundMaterial = playerMaterialTask.Result;
            PlayerTintIntensity = playerTintTask.Result;

            var lastFmCredentials = lastFmCredsTask.Result;
            LastFmUsername = lastFmCredentials?.Username;
            IsLastFmConnected = lastFmCredentials is not null && !string.IsNullOrEmpty(lastFmCredentials.Value.SessionKey);

            IsLastFmScrobblingEnabled = scrobblingTask.Result;
            IsLastFmNowPlayingEnabled = nowPlayingTask.Result;

            var authToken = lastFmAuthTokenTask.Result;
            IsConnectingToLastFm = !string.IsNullOrEmpty(authToken);

            ListenBrainzUserToken = lbTokenTask.Result ?? string.Empty;
            ListenBrainzIsConnected = !string.IsNullOrEmpty(ListenBrainzUserToken);
            ListenBrainzScrobblingEnabled = lbScrobblingTask.Result;
            ListenBrainzNowPlayingEnabled = lbNowPlayingTask.Result;
            ListenBrainzServerUrl = lbServerUrlTask.Result ?? string.Empty;
            ListenBrainzConnectionStatus = null;

            AccentColor = accentColorTask.Result ?? App.DefaultAccentColor;

            ArtistSplitCharacters = artistSplitTask.Result;
            GenreSplitCharacters = genreSplitTask.Result;

            // Initialize with a minimal list immediately so the binding is valid on first render.
            // The full language list is populated asynchronously to avoid blocking startup.
            AvailableLanguages.Clear();
            AvailableLanguages.Add(new LanguageModel(string.Empty, Nagi.WinUI.Resources.Strings.Language_Auto));
            AvailableLanguages.Add(new LanguageModel("en-US", "English"));

            var currentLangCode = languageTask.Result;
            SelectedLanguage = ResolveSelectedLanguage(currentLangCode);

            _ = PopulateAvailableLanguagesAsync(currentLangCode);

            LoadEqualizerState();

            // Load service providers
            var lyricsMapped = lyricsProvidersTask.Result
                .Select(ServiceProviderSettingViewModel.FromSetting)
                .ToList();
            foreach (var item in lyricsMapped)
                item.PropertyChanged += OnLyricsProviderPropertyChanged;
            LyricsProviders.ReplaceRange(lyricsMapped);

            var metadataProviders = metadataProvidersTask.Result
                .Select(ServiceProviderSettingViewModel.FromSetting)
                .OrderByDescending(provider => provider.Id == ServiceProviderIds.MusicBrainz)
                .ToList();

            foreach (var item in metadataProviders)
                item.PropertyChanged += OnMetadataProviderPropertyChanged;
            MetadataProviders.ReplaceRange(metadataProviders);
            _isLoaded = true;
        }
        finally
        {
            _isInitializing = false;
            _loadLock.Release();
        }
    }

    /// <summary>
    /// Resolves the best LanguageModel match for <paramref name="langCode"/> from AvailableLanguages.
    /// Must be called on the UI thread (reads the ObservableCollection).
    /// </summary>
    private LanguageModel ResolveSelectedLanguage(string langCode) =>
        AvailableLanguages.FirstOrDefault(l => string.Equals(l.Code, langCode, StringComparison.OrdinalIgnoreCase))
        ?? AvailableLanguages.FirstOrDefault(l => !string.IsNullOrEmpty(l.Code) && !string.IsNullOrEmpty(langCode) &&
                                                  (l.Code.StartsWith(langCode + "-", StringComparison.OrdinalIgnoreCase) || langCode.StartsWith(l.Code + "-", StringComparison.OrdinalIgnoreCase)))
        ?? AvailableLanguages.First(l => l.Code == string.Empty);

    [RelayCommand]
    private Task RefreshRomanizationPacksAsync()
    {
        return LoadRomanizationPacksAsync(forceRefresh: true);
    }

    public Task LoadRomanizationPacksAsync()
    {
        return LoadRomanizationPacksAsync(forceRefresh: false);
    }

    private async Task LoadRomanizationPacksAsync(bool forceRefresh)
    {
        if (_isDisposed) return;
        _romanizationPackCts?.Cancel();
        _romanizationPackCts?.Dispose();
        _romanizationPackCts = new CancellationTokenSource();
        var token = _romanizationPackCts.Token;

        IsLoadingRomanizationPacks = true;
        RomanizationPacksStatus = null;

        try
        {
            var packs = await _romanizationPackManager.GetAvailablePacksAsync(forceRefresh, token).ConfigureAwait(false);
            _dispatcherService.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested || _isDisposed) return;
                RomanizationPacks.ReplaceRange(packs.Select(p => new RomanizationPackViewModel(p)));
                RomanizationPacksStatus = RomanizationPacks.Count == 0
                    ? Nagi.WinUI.Resources.Strings.SettingsPage_RomanizationPacks_None
                    : null;
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh romanization packs.");
            _dispatcherService.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested || _isDisposed) return;
                RomanizationPacksStatus = string.Format(Nagi.WinUI.Resources.Strings.SettingsPage_RomanizationPacks_LoadFailedFormat, ex.Message);
            });
        }
        finally
        {
            _dispatcherService.TryEnqueue(() =>
            {
                if (!token.IsCancellationRequested && !_isDisposed) IsLoadingRomanizationPacks = false;
            });
        }
    }

    [RelayCommand]
    private async Task InstallRomanizationPackAsync(RomanizationPackViewModel? pack)
    {
        if (pack is null) return;

        pack.IsBusy = true;
        RomanizationPacksStatus = null;
        try
        {
            var result = await _romanizationPackManager.InstallPackAsync(pack.Id);
            if (!result.Succeeded)
            {
                RomanizationPacksStatus = result.ErrorMessage ?? Nagi.WinUI.Resources.Strings.SettingsPage_RomanizationPacks_InstallFailed;
                return;
            }

            await LoadRomanizationPacksAsync(forceRefresh: false);
        }
        finally
        {
            pack.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveRomanizationPackAsync(RomanizationPackViewModel? pack)
    {
        if (pack is null) return;

        pack.IsBusy = true;
        RomanizationPacksStatus = null;
        try
        {
            var result = await _romanizationPackManager.RemovePackAsync(pack.Id);
            if (!result.Succeeded)
            {
                RomanizationPacksStatus = result.ErrorMessage ?? Nagi.WinUI.Resources.Strings.SettingsPage_RomanizationPacks_RemoveFailed;
                return;
            }

            await LoadRomanizationPacksAsync(forceRefresh: false);
        }
        finally
        {
            pack.IsBusy = false;
        }
    }

    /// <summary>
    /// Populates AvailableLanguages asynchronously by scanning for satellite resource assemblies.
    /// Fires and forgets from LoadSettingsAsync — does not block startup.
    /// Updates SelectedLanguage once the full list is available.
    /// </summary>
    private async Task PopulateAvailableLanguagesAsync(string currentLangCode)
    {
        var initialSelection = SelectedLanguage;
        try
        {
            var languageCodes = await _appInfoService.GetAvailableLanguagesAsync().ConfigureAwait(false);
            var candidates = languageCodes.Select(code =>
                {
                    try
                    {
                        var culture = CultureInfo.GetCultureInfo(code);
                        return new LanguageModel(culture.Name, culture.NativeName);
                    }
                    catch (CultureNotFoundException) { return null; }
                })
                .OfType<LanguageModel>()
                .ToList();

            if (candidates.Count == 0) return;
            _dispatcherService.TryEnqueue(() =>
            {
                if (_isDisposed) return;
                var selection = ReferenceEquals(SelectedLanguage, initialSelection) ? currentLangCode : SelectedLanguage?.Code;
                ApplyWithoutSaving(() =>
                {
                    AvailableLanguages.ReplaceRange(AvailableLanguages.Concat(candidates)
                        .DistinctBy(language => language.Code, StringComparer.OrdinalIgnoreCase));
                    SelectedLanguage = ResolveSelectedLanguage(selection ?? string.Empty);
                });
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to populate available languages.");
        }
    }

    [RelayCommand]
    private async Task ResetApplicationDataAsync()
    {
        var confirmed = await _uiService.ShowConfirmationDialogAsync(
            Nagi.WinUI.Resources.Strings.Settings_Reset_Title,
            Nagi.WinUI.Resources.Strings.Settings_Reset_Message,
            Nagi.WinUI.Resources.Strings.Settings_Reset_Button,
            null);

        if (!confirmed) return;

        try
        {
            await _applicationLifecycle.ResetAndRestartAsync();
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Application reset failed");
            await _uiService.ShowMessageDialogAsync(
                Nagi.WinUI.Resources.Strings.Settings_ResetError_Title,
                string.Format(Nagi.WinUI.Resources.Strings.Settings_ResetError_Message, ex.Message));
        }
    }

    private bool IsLastFmAuthCurrent(int version) => !_isDisposed && version == _lastFmAuthVersion;

    [RelayCommand]
    private async Task LastFmInitialAuthAsync()
    {
        if (_isDisposed) return;
        var version = ++_lastFmAuthVersion;
        IsConnectingToLastFm = true;
        try
        {
            var authData = await _lastFmAuthService.GetAuthenticationTokenAsync();
            if (!IsLastFmAuthCurrent(version)) return;
            if (authData is { Token: not null, AuthUrl: not null })
            {
                await _settingsService.SaveLastFmAuthTokenAsync(authData.Value.Token);
                if (await Launcher.LaunchUriAsync(new Uri(authData.Value.AuthUrl))) return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start Last.fm authentication.");
        }

        if (!IsLastFmAuthCurrent(version)) return;
        IsConnectingToLastFm = false;
        await SaveSettingAsync(() => _settingsService.SaveLastFmAuthTokenAsync(null));
        await _uiService.ShowMessageDialogAsync(Strings.Settings_LastFm_AuthError_Title, Strings.Settings_LastFm_AuthError_Message);
    }

    [RelayCommand]
    private async Task LastFmFinalizeAuthAsync()
    {
        if (_isDisposed) return;
        var version = _lastFmAuthVersion;
        try
        {
            var authToken = await _settingsService.GetLastFmAuthTokenAsync();
            if (string.IsNullOrEmpty(authToken)) return;

            var sessionData = await _lastFmAuthService.GetSessionAsync(authToken);
            if (!IsLastFmAuthCurrent(version)) return;
            if (sessionData is { Username: not null, SessionKey: not null })
            {
                await _settingsService.SaveLastFmCredentialsAsync(sessionData.Value.Username, sessionData.Value.SessionKey);
                await Task.WhenAll(_settingsService.SetLastFmScrobblingEnabledAsync(true),
                    _settingsService.SetLastFmNowPlayingEnabledAsync(true));

                ApplyWithoutSaving(() =>
                {
                    LastFmUsername = sessionData.Value.Username;
                    IsLastFmConnected = true;
                    IsLastFmScrobblingEnabled = true;
                    IsLastFmNowPlayingEnabled = true;
                });
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to finish Last.fm authentication.");
        }
        finally
        {
            if (IsLastFmAuthCurrent(version))
            {
                IsConnectingToLastFm = false;
                await SaveSettingAsync(() => _settingsService.SaveLastFmAuthTokenAsync(null));
            }
        }

        if (IsLastFmAuthCurrent(version))
            await _uiService.ShowMessageDialogAsync(Strings.Settings_LastFm_FinalizeError_Title,
                Strings.Settings_LastFm_FinalizeError_Message);
    }

    [RelayCommand]
    private async Task CancelLastFmAuthAsync()
    {
        ++_lastFmAuthVersion;
        IsConnectingToLastFm = false;
        await SaveSettingAsync(() => _settingsService.SaveLastFmAuthTokenAsync(null));
    }

    [RelayCommand]
    private async Task LastFmDisconnectAsync()
    {
        var confirmed = await _uiService.ShowConfirmationDialogAsync(
            Nagi.WinUI.Resources.Strings.Settings_LastFm_Disconnect_Title,
            Nagi.WinUI.Resources.Strings.Settings_LastFm_Disconnect_Message,
            Nagi.WinUI.Resources.Strings.Settings_LastFm_Disconnect_Button,
            null);

        if (!confirmed) return;

        ++_lastFmAuthVersion;
        try
        {
            await _settingsService.ClearLastFmCredentialsAsync();
            ApplyWithoutSaving(() =>
            {
                IsLastFmConnected = false;
                LastFmUsername = null;
                IsConnectingToLastFm = false;
                IsLastFmScrobblingEnabled = false;
                IsLastFmNowPlayingEnabled = false;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to disconnect Last.fm.");
        }
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

    [RelayCommand]
    private async Task ResetPlayerButtonsAsync()
    {
        _playerButtonSaveDebouncer.Cancel();
        var defaultButtons = _settingsService.GetDefaultPlayerButtonSettings();
        ApplyWithoutSaving(() =>
        {
            foreach (var item in PlayerButtons) item.PropertyChanged -= OnPlayerButtonPropertyChanged;
            foreach (var button in defaultButtons) button.PropertyChanged += OnPlayerButtonPropertyChanged;
            PlayerButtons.ReplaceRange(defaultButtons);
        });

        await _settingsService.SetPlayerButtonSettingsAsync(defaultButtons);
    }

    [RelayCommand]
    private async Task ExportAllPlaylistsAsync()
    {
        var folderPath = await _uiService.PickSingleFolderAsync();
        if (folderPath is null) return;

        var result = await _playlistExportService.ExportAllPlaylistsAsync(folderPath);

        if (result.Success)
        {
            await _uiService.ShowMessageDialogAsync(Nagi.WinUI.Resources.Strings.Settings_Export_Success_Title,
                ResourceFormatter.Format(Nagi.WinUI.Resources.Strings.Settings_Export_Success_Message, result.PlaylistsExported, result.TotalSongs, folderPath));
        }
        else
        {
            await _uiService.ShowMessageDialogAsync(Nagi.WinUI.Resources.Strings.Settings_Export_Failed_Title, result.ErrorMessage ?? Nagi.WinUI.Resources.Strings.Settings_Export_Failed_Message);
        }
    }

    [RelayCommand]
    private async Task ImportMultiplePlaylistsAsync()
    {
        var filePaths = await _uiService.PickOpenMultipleFilesAsync([".m3u", ".m3u8"]);
        if (filePaths.Count == 0) return;

        var result = await _playlistExportService.ImportMultiplePlaylistsAsync(filePaths);

        if (result.Success)
        {
            var message = ResourceFormatter.Format(Nagi.WinUI.Resources.Strings.Settings_Import_Success_Message, result.PlaylistsImported, result.TotalMatchedSongs);
            if (result.TotalUnmatchedSongs > 0)
            {
                message += ResourceFormatter.Format(Nagi.WinUI.Resources.Strings.Settings_Import_Unmatched_Message, result.TotalUnmatchedSongs);
            }
            if (result.FailedFiles.Count > 0)
            {
                message += ResourceFormatter.Format(Nagi.WinUI.Resources.Strings.Settings_Import_FailedFiles_Message, result.FailedFiles.Count);
            }
            await _uiService.ShowMessageDialogAsync(Nagi.WinUI.Resources.Strings.Settings_Import_Success_Title, message);
        }
        else
        {
            await _uiService.ShowMessageDialogAsync(Nagi.WinUI.Resources.Strings.Settings_Import_FailedTotal_Title, Nagi.WinUI.Resources.Strings.Settings_Import_FailedTotal_Message);
        }
    }

    [RelayCommand]
    private async Task CreateBackupAsync()
    {
        var folderPath = await _uiService.PickSingleFolderAsync();
        if (folderPath is null) return;

        var result = await _backupRestoreService.CreateBackupAsync(folderPath);

        if (result.Success)
        {
            await _uiService.ShowMessageDialogAsync(
                Resources.Strings.Settings_Backup_Success_Title,
                string.Format(Resources.Strings.Settings_Backup_Success_Message, result.BackupSizeMB, result.BackupFilePath));
        }
        else
        {
            await _uiService.ShowMessageDialogAsync(
                Resources.Strings.Settings_Backup_Failed_Title,
                result.ErrorMessage ?? Resources.Strings.Settings_Backup_Failed_Message);
        }
    }

    [RelayCommand]
    private async Task RestoreFromBackupAsync()
    {
        var backupFilePath = await _uiService.PickSingleFileAsync(new[] { ".zip" });
        if (string.IsNullOrEmpty(backupFilePath)) return;

        // Validate backup
        var isValid = await _backupRestoreService.ValidateBackupAsync(backupFilePath);
        if (!isValid)
        {
            await _uiService.ShowMessageDialogAsync(
                Resources.Strings.Settings_Restore_Invalid_Title,
                Resources.Strings.Settings_Restore_Invalid_Message);
            return;
        }

        // Confirmation dialog
        var confirmed = await _uiService.ShowConfirmationDialogAsync(
            Resources.Strings.Settings_Restore_Confirm_Title,
            Resources.Strings.Settings_Restore_Confirm_Message,
            Resources.Strings.Settings_Restore_Confirm_Button,
            Resources.Strings.Generic_Cancel);

        if (!confirmed) return;

        var result = await _backupRestoreService.RestoreFromBackupAsync(backupFilePath);

        if (result.Success)
        {
            await _uiService.ShowMessageDialogAsync(
                Resources.Strings.Settings_Restore_Success_Title,
                Resources.Strings.Settings_Restore_Success_Message);

            // Restart app
            if (result.RequiresRestart)
            {
                AppInstance.Restart("");
            }
        }
        else
        {
            await _uiService.ShowMessageDialogAsync(
                Resources.Strings.Settings_Restore_Failed_Title,
                result.ErrorMessage ?? Resources.Strings.Settings_Restore_Failed_Message);
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

    private void OnPlayerButtonsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        if (e.NewItems != null)
            foreach (PlayerButtonSetting item in e.NewItems)
                item.PropertyChanged += OnPlayerButtonPropertyChanged;

        if (e.OldItems != null)
            foreach (PlayerButtonSetting item in e.OldItems)
                item.PropertyChanged -= OnPlayerButtonPropertyChanged;

        QueuePlayerButtonSave();
    }

    private void OnPlayerButtonPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isInitializing || e.PropertyName != nameof(PlayerButtonSetting.IsEnabled)) return;
        QueuePlayerButtonSave();
    }

    /// <summary>
    ///     Queues a debounced save for player button settings to prevent rapid saves during drag operations.
    /// </summary>
    private void QueuePlayerButtonSave()
    {
        var snapshot = PlayerButtons.ToList();
        QueueSettingsSave(_playerButtonSaveDebouncer, _ => _settingsService.SetPlayerButtonSettingsAsync(snapshot), nameof(PlayerButtons));
    }

    private void QueueSettingsSave(Debouncer debouncer, Func<CancellationToken, Task> save, string settingName)
    {
        if (_isDisposed) return;
        debouncer.Trigger(async token =>
        {
            try
            {
                await _dispatcherService.EnqueueAsync(() => token.IsCancellationRequested || _isDisposed
                    ? Task.CompletedTask : save(token));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to save setting {SettingName}", settingName);
            }
        });
    }

    internal Task PendingSettingsSaves => Task.WhenAll(
        _preampDebouncer.PendingInvocation,
        _playerButtonSaveDebouncer.PendingInvocation,
        _navigationItemSaveDebouncer.PendingInvocation,
        _lyricsProviderSaveDebouncer.PendingInvocation,
        _metadataProviderSaveDebouncer.PendingInvocation,
        _artistSplitSaveDebouncer.PendingInvocation,
        _genreSplitSaveDebouncer.PendingInvocation,
        _playerTintSaveDebouncer.PendingInvocation,
        _accentColorSaveDebouncer.PendingInvocation);

    private void ApplyWithoutSaving(Action update)
    {
        var wasInitializing = _isInitializing;
        _isInitializing = true;
        try { update(); }
        finally { _isInitializing = wasInitializing; }
    }

    private async Task SaveSettingAsync(Func<Task> save, [CallerMemberName] string? settingName = null)
    {
        if (_isInitializing || _isDisposed) return;
        try
        {
            await save();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save setting {SettingName}", settingName);
        }
    }

    private bool CanRescanMetadata() => !_playerViewModel.IsGlobalOperationInProgress;

    private void OnPlayerViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.IsGlobalOperationInProgress))
        {
            RescanMetadataCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRescanMetadata))]
    private async Task RescanMetadataAsync()
    {
        if (_playerViewModel.IsGlobalOperationInProgress) return;

        var confirmed = await _uiService.ShowConfirmationDialogAsync(
            Nagi.WinUI.Resources.Strings.Settings_Dialog_Rescan_Title,
            Nagi.WinUI.Resources.Strings.Settings_Dialog_Rescan_Content,
            Nagi.WinUI.Resources.Strings.Settings_Dialog_Rescan_PrimaryButton,
            null);

        if (!confirmed || _isDisposed || _playerViewModel.IsGlobalOperationInProgress) return;

        using var rescanCts = new CancellationTokenSource();
        _metadataRescanCts = rescanCts;
        var token = rescanCts.Token;

        _playerViewModel.IsGlobalOperationInProgress = true;
        _playerViewModel.GlobalOperationStatusMessage = Nagi.WinUI.Resources.Strings.Settings_Status_Rescan_Preparing;
        _playerViewModel.IsGlobalOperationIndeterminate = true;
        _playerViewModel.SetGlobalOperationCancellation(rescanCts.Cancel);

        try
        {
            await SaveMetadataSplitSettingsAsync();
            token.ThrowIfCancellationRequested();
            await _libraryScanner.ForceRescanMetadataAsync(CreateScanProgress(token), token);
            token.ThrowIfCancellationRequested();

            await _uiService.ShowMessageDialogAsync(Nagi.WinUI.Resources.Strings.Settings_Dialog_RescanComplete_Title, Nagi.WinUI.Resources.Strings.Settings_Dialog_RescanComplete_Content);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Metadata rescan cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Metadata rescan failed.");
            await _uiService.ShowMessageDialogAsync(Nagi.WinUI.Resources.Strings.Settings_Dialog_RescanFailed_Title, Nagi.WinUI.Resources.Strings.Settings_Dialog_RescanFailed_Content);
        }
        finally
        {
            rescanCts.Cancel();
            _metadataRescanCts = null;
            _playerViewModel.SetGlobalOperationCancellation(null);
            _playerViewModel.IsGlobalOperationInProgress = false;
            _playerViewModel.IsGlobalOperationIndeterminate = false;
        }
    }

    private async Task SaveMetadataSplitSettingsAsync()
    {
        _artistSplitSaveDebouncer.Cancel();
        _genreSplitSaveDebouncer.Cancel();

        await Task.WhenAll(
            _settingsService.SetArtistSplitCharactersAsync(ArtistSplitCharacters),
            _settingsService.SetGenreSplitCharactersAsync(GenreSplitCharacters));
    }

    private void OnNavigationItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (NavigationItemSetting item in e.NewItems)
                item.PropertyChanged += OnNavigationItemPropertyChanged;

        if (e.OldItems != null)
            foreach (NavigationItemSetting item in e.OldItems)
                item.PropertyChanged -= OnNavigationItemPropertyChanged;

        if (!_isInitializing)
            QueueNavigationItemSave();
    }

    private void OnNavigationItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isInitializing || e.PropertyName != nameof(NavigationItemSetting.IsEnabled)) return;
        QueueNavigationItemSave();
    }

    /// <summary>
    ///     Queues a debounced save for navigation items to prevent rapid saves.
    /// </summary>
    private void QueueNavigationItemSave()
    {
        var snapshot = NavigationItems.ToList();
        QueueSettingsSave(_navigationItemSaveDebouncer, _ => _settingsService.SetNavigationItemsAsync(snapshot), nameof(NavigationItems));
    }

    #region Service Provider Handlers

    private void OnLyricsProvidersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (ServiceProviderSettingViewModel item in e.NewItems)
                item.PropertyChanged += OnLyricsProviderPropertyChanged;

        if (e.OldItems != null)
            foreach (ServiceProviderSettingViewModel item in e.OldItems)
                item.PropertyChanged -= OnLyricsProviderPropertyChanged;

        if (!_isInitializing)
            QueueLyricsProviderSave();
    }

    private void OnLyricsProviderPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isInitializing || e.PropertyName != nameof(ServiceProviderSettingViewModel.IsEnabled)) return;
        QueueLyricsProviderSave();
    }

    private void QueueLyricsProviderSave()
    {
        var snapshot = LyricsProviders.Select((vm, i) => vm.ToSetting(i)).ToList();
        QueueSettingsSave(_lyricsProviderSaveDebouncer, _ => _settingsService.SetServiceProvidersAsync(ServiceCategory.Lyrics, snapshot), nameof(LyricsProviders));
    }

    private void OnMetadataProvidersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (ServiceProviderSettingViewModel item in e.NewItems)
                item.PropertyChanged += OnMetadataProviderPropertyChanged;

        if (e.OldItems != null)
            foreach (ServiceProviderSettingViewModel item in e.OldItems)
                item.PropertyChanged -= OnMetadataProviderPropertyChanged;

        if (!_isInitializing)
        {
            _dispatcherService.TryEnqueue(() =>
            {
                if (_isDisposed) return;
                var musicBrainz = MetadataProviders.FirstOrDefault(provider => provider.Id == ServiceProviderIds.MusicBrainz);
                var musicBrainzIndex = musicBrainz is null ? -1 : MetadataProviders.IndexOf(musicBrainz);

                if (musicBrainzIndex > 0)
                {
                    ApplyWithoutSaving(() => MetadataProviders.Move(musicBrainzIndex, 0));
                }

                QueueMetadataProviderSave();
            });
        }
    }

    private void OnMetadataProviderPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isInitializing || e.PropertyName != nameof(ServiceProviderSettingViewModel.IsEnabled)) return;

        if (sender is ServiceProviderSettingViewModel provider)
        {
            // If MusicBrainz is disabled, disable dependent services
            if (provider.Id == ServiceProviderIds.MusicBrainz && !provider.IsEnabled)
            {
                var dependents = MetadataProviders.Where(p => p.Id == ServiceProviderIds.TheAudioDb || p.Id == ServiceProviderIds.FanartTv).ToList();
                foreach (var dependent in dependents)
                {
                    dependent.IsEnabled = false;
                }
            }
            // If trying to enable a dependent service while MusicBrainz is disabled, revert it
            else if ((provider.Id == ServiceProviderIds.TheAudioDb || provider.Id == ServiceProviderIds.FanartTv) && provider.IsEnabled)
            {
                var musicBrainz = MetadataProviders.FirstOrDefault(p => p.Id == ServiceProviderIds.MusicBrainz);
                if (musicBrainz is not { IsEnabled: true })
                {
                    provider.IsEnabled = false;
                }
            }
        }

        QueueMetadataProviderSave();
    }

    private void QueueMetadataProviderSave()
    {
        var snapshot = MetadataProviders.Select((vm, i) => vm.ToSetting(i)).ToList();
        QueueSettingsSave(_metadataProviderSaveDebouncer, _ => _settingsService.SetServiceProvidersAsync(ServiceCategory.Metadata, snapshot), nameof(MetadataProviders));
    }

    #endregion

    async partial void OnSelectedThemeChanged(ElementTheme value)
    {
        if (_isInitializing) return;
        try
        {
            await _settingsService.SetThemeAsync(value);
            _themeService.ApplyTheme(value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error changing app theme to {Theme}", value);
        }
    }

    partial void OnSelectedBackdropMaterialChanged(BackdropMaterial value) =>
        _ = SaveSettingAsync(() => _settingsService.SetBackdropMaterialAsync(value));

    async partial void OnIsDynamicThemingEnabledChanged(bool value)
    {
        if (_isInitializing) return;
        try
        {
            await _settingsService.SetDynamicThemingAsync(value);
            if (value)
            {
                await _themeService.ReapplyCurrentDynamicThemeAsync();
            }
            else
            {
                var accentColor = await _settingsService.GetAccentColorAsync();
                await _themeService.ApplyAccentColorAsync(accentColor);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating dynamic theming to {Value}", value);
        }
    }

    partial void OnIsPlayerAnimationEnabledChanged(bool value) =>
        _ = SaveSettingAsync(() => _settingsService.SetPlayerAnimationEnabledAsync(value));

    partial void OnIsRestorePlaybackStateEnabledChanged(bool value) =>
        _ = SaveSettingAsync(() => _settingsService.SetRestorePlaybackStateEnabledAsync(value));

    async partial void OnIsAutoLaunchEnabledChanged(bool value)
    {
        if (_isInitializing || _isDisposed || IsUpdatingAutoLaunch) return;
        IsUpdatingAutoLaunch = true;
        try
        {
            await _settingsService.SetAutoLaunchEnabledAsync(value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to change startup setting.");
        }

        try
        {
            var enabled = await _settingsService.GetAutoLaunchEnabledAsync();
            if (!_isDisposed) IsAutoLaunchEnabled = enabled;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read startup setting.");
        }
        finally
        {
            IsUpdatingAutoLaunch = false;
        }
    }

    partial void OnIsStartMinimizedEnabledChanged(bool value) =>
        _ = SaveSettingAsync(() => _settingsService.SetStartMinimizedEnabledAsync(value));

    partial void OnIsHideToTrayEnabledChanged(bool value) =>
        _ = SaveSettingAsync(() => _settingsService.SetHideToTrayEnabledAsync(value));

    partial void OnIsMinimizeToMiniPlayerEnabledChanged(bool value) =>
        _ = SaveSettingAsync(() => _settingsService.SetMinimizeToMiniPlayerEnabledAsync(value));

    partial void OnIsShowCoverArtInTrayFlyoutEnabledChanged(bool value) =>
        _ = SaveSettingAsync(() => _settingsService.SetShowCoverArtInTrayFlyoutAsync(value));

    partial void OnIsFetchOnlineMetadataEnabledChanged(bool value) =>
        _ = SaveSettingAsync(() => _settingsService.SetFetchOnlineMetadataEnabledAsync(value));

    partial void OnIsIgnoreLeadingArticlesOnSortEnabledChanged(bool value) =>
        _ = SaveSettingAsync(() => _settingsService.SetIgnoreLeadingArticlesOnSortEnabledAsync(value));

    partial void OnIsFetchOnlineLyricsEnabledChanged(bool value) =>
        _ = SaveSettingAsync(() => _settingsService.SetFetchOnlineLyricsEnabledAsync(value));

    partial void OnIsLyricsRomanizationEnabledChanged(bool value) =>
        _ = SaveSettingAsync(() => _settingsService.SetLyricsRomanizationEnabledAsync(value));

    partial void OnIsDiscordRichPresenceEnabledChanged(bool value) =>
        _ = SaveSettingAsync(() => _settingsService.SetDiscordRichPresenceEnabledAsync(value));


    partial void OnIsRememberWindowSizeEnabledChanged(bool value) =>
        _ = SaveSettingAsync(() => _settingsService.SetRememberWindowSizeEnabledAsync(value));

    partial void OnIsRememberWindowPositionEnabledChanged(bool value) =>
        _ = SaveSettingAsync(() => _settingsService.SetRememberWindowPositionEnabledAsync(value));

    partial void OnIsRememberPaneStateEnabledChanged(bool value) =>
        _ = SaveSettingAsync(() => _settingsService.SetRememberPaneStateEnabledAsync(value));

    async partial void OnIsVolumeNormalizationEnabledChanged(bool value)
    {
        if (_isInitializing || _isDisposed) return;
        var version = ++_volumeNormalizationVersion;
        bool IsCurrent() => !_isDisposed && version == _volumeNormalizationVersion;

        try
        {
            if (!value)
            {
                _replayGainScanCts?.Cancel();
                await _settingsService.SetVolumeNormalizationEnabledAsync(false);
                return;
            }

            var ffmpegInstalled = await _ffmpegService.IsFFmpegInstalledAsync();
            if (!IsCurrent()) return;
            if (!ffmpegInstalled)
            {
                ffmpegInstalled = await _uiService.ShowFFmpegSetupDialogAsync(
                    Strings.Settings_Dialog_FFmpegNotFound_Title,
                    _ffmpegService.GetFFmpegSetupInstructions(),
                    () => _ffmpegService.IsFFmpegInstalledAsync(forceRecheck: true));
                if (!IsCurrent()) return;
                if (!ffmpegInstalled)
                {
                    IsVolumeNormalizationEnabled = false;
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
                IsVolumeNormalizationEnabled = false;
                return;
            }

            await _settingsService.SetVolumeNormalizationEnabledAsync(true);
            if (IsCurrent()) await ScanLibraryForReplayGainAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error toggling volume normalization to {Value}", value);
            if (value && IsCurrent()) IsVolumeNormalizationEnabled = false;
        }
    }

    partial void OnIsWasapiExclusiveModeEnabledChanged(bool value) =>
        _ = SaveSettingAsync(() => _settingsService.SetWasapiExclusiveModeEnabledAsync(value));

    partial void OnIsFadeOnPlayPauseEnabledChanged(bool value) =>
        _ = SaveSettingAsync(() => _settingsService.SetFadeOnPlayPauseEnabledAsync(value));

    partial void OnFadeInDurationMsChanged(double value) =>
        _ = SaveSettingAsync(() => _settingsService.SetFadeInDurationMsAsync((int)value));

    partial void OnFadeOutDurationMsChanged(double value) =>
        _ = SaveSettingAsync(() => _settingsService.SetFadeOutDurationMsAsync((int)value));

    /// <summary>
    ///     Scans the library for songs missing ReplayGain data and writes the tags.
    /// </summary>
    private async Task ScanLibraryForReplayGainAsync()
    {
        if (_isDisposed || _playerViewModel.IsGlobalOperationInProgress) return;

        using var scanCts = new CancellationTokenSource();
        _replayGainScanCts = scanCts;
        var cancellationToken = scanCts.Token;

        _playerViewModel.IsGlobalOperationInProgress = true;
        _playerViewModel.GlobalOperationStatusMessage = Nagi.WinUI.Resources.Strings.Settings_Status_VolumeNorm_Preparing;
        _playerViewModel.IsGlobalOperationIndeterminate = true;
        _playerViewModel.SetGlobalOperationCancellation(scanCts.Cancel);

        try
        {
            await _replayGainService.ScanLibraryAsync(CreateScanProgress(cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _playerViewModel.GlobalOperationStatusMessage = Nagi.WinUI.Resources.Strings.Settings_Status_VolumeNorm_Cancelled;
        }
        catch (Exception ex)
        {
            _playerViewModel.GlobalOperationStatusMessage = string.Format(Nagi.WinUI.Resources.Strings.Settings_Status_VolumeNorm_Error_Format, ex.Message);
            _logger.LogError(ex, "Failed to scan library for ReplayGain");
        }
        finally
        {
            scanCts.Cancel();
            _replayGainScanCts = null;
            _playerViewModel.SetGlobalOperationCancellation(null);
            _playerViewModel.IsGlobalOperationInProgress = false;
            _playerViewModel.IsGlobalOperationIndeterminate = false;
        }
    }

    private IProgress<ScanProgress> CreateScanProgress(CancellationToken token) => new Progress<ScanProgress>(progress =>
    {
        if (token.IsCancellationRequested || _isDisposed) return;
        _playerViewModel.GlobalOperationStatusMessage = progress.StatusText;
        _playerViewModel.IsGlobalOperationIndeterminate = progress.IsIndeterminate || progress.Percentage < 5;
        _playerViewModel.GlobalOperationProgressValue = progress.Percentage;
    });

    partial void OnIsLastFmScrobblingEnabledChanged(bool value) =>
        _ = SaveSettingAsync(() => _settingsService.SetLastFmScrobblingEnabledAsync(value));

    partial void OnIsLastFmNowPlayingEnabledChanged(bool value) =>
        _ = SaveSettingAsync(() => _settingsService.SetLastFmNowPlayingEnabledAsync(value));

    async partial void OnListenBrainzScrobblingEnabledChanged(bool value)
    {
        if (_isInitializing) return;
        try
        {
            await _settingsService.SetListenBrainzScrobblingEnabledAsync(value);
            if (value) await StampListenBrainzEnabledSinceIfNeededAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist ListenBrainz scrobbling toggle");
        }
    }

    async partial void OnListenBrainzNowPlayingEnabledChanged(bool value)
    {
        if (_isInitializing) return;
        try
        {
            await _settingsService.SetListenBrainzNowPlayingEnabledAsync(value);
            if (value) await StampListenBrainzEnabledSinceIfNeededAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist ListenBrainz now-playing toggle");
        }
    }

    async partial void OnListenBrainzServerUrlChanged(string value)
    {
        if (_isInitializing) return;
        ++_listenBrainzConnectionVersion;
        try
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            await _settingsService.SetListenBrainzServerUrlAsync(normalized);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist ListenBrainz server URL");
        }
    }

    /// <summary>
    ///     Stamps <see cref="ISettingsService.SetListenBrainzEnabledSinceUtcAsync"/> with the current UTC time
    ///     the first time any ListenBrainz submission is enabled, so historic listens are not backfilled.
    /// </summary>
    private async Task StampListenBrainzEnabledSinceIfNeededAsync()
    {
        try
        {
            if (await _settingsService.GetListenBrainzEnabledSinceUtcAsync() is null)
            {
                await _settingsService.SetListenBrainzEnabledSinceUtcAsync(DateTime.UtcNow);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stamp ListenBrainz EnabledSinceUtc");
        }
    }

    [RelayCommand]
    private async Task TestListenBrainzConnectionAsync()
    {
        if (_isDisposed) return;
        var version = ++_listenBrainzConnectionVersion;
        try
        {
            var token = ListenBrainzUserToken;
            var server = string.IsNullOrWhiteSpace(ListenBrainzServerUrl) ? null : ListenBrainzServerUrl;
            var result = await _listenBrainzScrobblerService.ValidateTokenAsync(token, server);
            if (_isDisposed || version != _listenBrainzConnectionVersion) return;
            if (result.IsValid)
            {
                await _settingsService.SaveListenBrainzUserTokenAsync(token);
                await StampListenBrainzEnabledSinceIfNeededAsync();
                ListenBrainzIsConnected = true;
                ListenBrainzConnectionStatus = string.Format(
                    Nagi.WinUI.Resources.Strings.SettingsPage_ListenBrainz_Connected,
                    result.Username);
            }
            else
            {
                ListenBrainzIsConnected = false;
                ListenBrainzConnectionStatus = string.Format(
                    Nagi.WinUI.Resources.Strings.SettingsPage_ListenBrainz_ConnectionFailed,
                    result.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ListenBrainz token validation failed");
            if (_isDisposed || version != _listenBrainzConnectionVersion) return;
            ListenBrainzIsConnected = false;
            ListenBrainzConnectionStatus = string.Format(
                Nagi.WinUI.Resources.Strings.SettingsPage_ListenBrainz_ConnectionFailed,
                "Could not reach ListenBrainz.");
        }
    }

    [RelayCommand]
    private async Task DisconnectListenBrainzAsync()
    {
        var confirmed = await _uiService.ShowConfirmationDialogAsync(
            Nagi.WinUI.Resources.Strings.SettingsPage_ListenBrainz_Disconnect_Title,
            Nagi.WinUI.Resources.Strings.SettingsPage_ListenBrainz_Disconnect_Content,
            Nagi.WinUI.Resources.Strings.SettingsPage_ListenBrainz_Disconnect_Button,
            null);

        if (!confirmed || _isDisposed) return;
        ++_listenBrainzConnectionVersion;

        try
        {
            await _settingsService.ClearListenBrainzUserTokenAsync();
            await _settingsService.SetListenBrainzScrobblingEnabledAsync(false);
            await _settingsService.SetListenBrainzNowPlayingEnabledAsync(false);
            await _settingsService.SetListenBrainzEnabledSinceUtcAsync(null);

            ApplyWithoutSaving(() =>
            {
                ListenBrainzScrobblingEnabled = false;
                ListenBrainzNowPlayingEnabled = false;
            });

            ListenBrainzUserToken = string.Empty;
            ListenBrainzIsConnected = false;
            ListenBrainzConnectionStatus = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to disconnect ListenBrainz");
        }
    }

    partial void OnListenBrainzUserTokenChanged(string value) => ++_listenBrainzConnectionVersion;

    partial void OnEqualizerPreampChanged(float value)
    {
        if (_isInitializing) return;
        QueueSettingsSave(_preampDebouncer, _ => _playbackService.SetEqualizerPreampAsync(value), nameof(EqualizerPreamp));
    }
    partial void OnAccentColorChanged(Windows.UI.Color value)
    {
        if (_isInitializing) return;
        QueueSettingsSave(_accentColorSaveDebouncer, async token =>
        {
            await _settingsService.SetAccentColorAsync(value);
            token.ThrowIfCancellationRequested();
            if (!IsDynamicThemingEnabled)
                await _themeService.ApplyAccentColorAsync(value);
        }, nameof(AccentColor));
    }

    [RelayCommand]
    private async Task ResetAccentColorAsync()
    {
        _accentColorSaveDebouncer.Cancel();
        ApplyWithoutSaving(() => AccentColor = App.DefaultAccentColor);

        await _settingsService.SetAccentColorAsync(null);
        if (!IsDynamicThemingEnabled)
        {
            await _themeService.ApplyAccentColorAsync(null);
        }
    }

    [RelayCommand]
    private async Task ResetPlayerDesignSettingsAsync()
    {
        _playerTintSaveDebouncer.Cancel();
        ApplyWithoutSaving(() =>
        {
            SelectedPlayerBackgroundMaterial = SettingsDefaults.DefaultPlayerBackgroundMaterial;
            PlayerTintIntensity = SettingsDefaults.DefaultPlayerTintIntensity;
        });

        await _settingsService.SetPlayerBackgroundMaterialAsync(SettingsDefaults.DefaultPlayerBackgroundMaterial);
        await _settingsService.SetPlayerTintIntensityAsync(SettingsDefaults.DefaultPlayerTintIntensity);
    }
}

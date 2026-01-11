using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Models;
using Nagi.WinUI.Models;
using Nagi.WinUI.Navigation;
using Nagi.WinUI.Services;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     ViewModel for a single band in the audio equalizer.
/// </summary>
public partial class EqualizerBandViewModel : ObservableObject, IDisposable
{
    private const int KiloHertzThreshold = 1000;

    private CancellationTokenSource? _debounceCts;

    public EqualizerBandViewModel(uint index, float frequency, float initialGain)
    {
        Index = index;
        FrequencyLabel = frequency < KiloHertzThreshold ? $"{frequency:F0}" : $"{frequency / KiloHertzThreshold:F0}K";
        Gain = initialGain;
    }

    public uint Index { get; }
    public string FrequencyLabel { get; }
    
    /// <summary>
    /// Indicates whether the update comes from an external source (e.g. service event).
    /// If true, we should skip saving changes back to the service.
    /// </summary>
    public bool IsExternalUpdate { get; set; }

    [ObservableProperty] public partial float Gain { get; set; }
    
    public event Action<EqualizerBandViewModel>? GainChanged;
    public event Action<EqualizerBandViewModel, float>? GainCommitted;

    public void Dispose()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Debounces gain changes to prevent excessive updates to the audio player,
    ///     improving performance when a slider is being dragged.
    /// </summary>
    async partial void OnGainChanged(float value)
    {
        if (IsExternalUpdate) return;
        
        GainChanged?.Invoke(this);

        _debounceCts?.Cancel();
        _debounceCts?.Dispose();

        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        try
        {
            await Task.Delay(SettingsViewModel.EqualizerDebounceDelayMilliseconds, token);
            GainCommitted?.Invoke(this, value);
        }
        catch (TaskCanceledException)
        {
            // Debounce was cancelled, which is expected behavior.
        }
    }
}




/// <summary>
///     ViewModel for the Settings page, providing properties and commands to manage application settings.
/// </summary>
public partial class SettingsViewModel : ObservableObject, IDisposable
{
    internal const int EqualizerDebounceDelayMilliseconds = 300;

    private readonly IAppInfoService _appInfoService;
    private readonly IApplicationLifecycle _applicationLifecycle;
    private readonly ILastFmAuthService _lastFmAuthService;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly IMusicPlaybackService _playbackService;
    private readonly IUISettingsService _settingsService;
    private readonly IThemeService _themeService;
    private readonly IUIService _uiService;
    private readonly IUpdateService _updateService;
    private readonly IPlaylistExportService _playlistExportService;
    private readonly ILibraryReader _libraryReader;
    private readonly PlayerViewModel _playerViewModel;
    private readonly IReplayGainService _replayGainService;
    private readonly IFFmpegService _ffmpegService;
    private readonly IDispatcherService _dispatcherService;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    private const int SettingsSaveDebounceMs = 300;

    private bool _isDisposed;
    private bool _isInitializing;
    private CancellationTokenSource? _preampDebounceCts;
    private CancellationTokenSource? _replayGainScanCts;
    private CancellationTokenSource? _playerButtonSaveCts;
    private CancellationTokenSource? _navigationItemSaveCts;
    private CancellationTokenSource? _lyricsProviderSaveCts;
    private CancellationTokenSource? _metadataProviderSaveCts;

    public SettingsViewModel(
        IUISettingsService settingsService,
        IUIService uiService,
        IThemeService themeService,
        IApplicationLifecycle applicationLifecycle,
        IAppInfoService appInfoService,
        IUpdateService updateService,
        ILastFmAuthService lastFmAuthService,
        IMusicPlaybackService playbackService,
        IPlaylistExportService playlistExportService,
        ILibraryReader libraryReader,
        PlayerViewModel playerViewModel,
        IReplayGainService replayGainService,
        IFFmpegService ffmpegService,
        IDispatcherService dispatcherService,
        ILogger<SettingsViewModel> logger)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _uiService = uiService ?? throw new ArgumentNullException(nameof(uiService));
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        _applicationLifecycle = applicationLifecycle ?? throw new ArgumentNullException(nameof(applicationLifecycle));
        _appInfoService = appInfoService ?? throw new ArgumentNullException(nameof(appInfoService));
        _updateService = updateService ?? throw new ArgumentNullException(nameof(updateService));
        _lastFmAuthService = lastFmAuthService ?? throw new ArgumentNullException(nameof(lastFmAuthService));
        _playbackService = playbackService ?? throw new ArgumentNullException(nameof(playbackService));
        _playlistExportService = playlistExportService ?? throw new ArgumentNullException(nameof(playlistExportService));
        _libraryReader = libraryReader ?? throw new ArgumentNullException(nameof(libraryReader));
        _playerViewModel = playerViewModel ?? throw new ArgumentNullException(nameof(playerViewModel));
        _replayGainService = replayGainService ?? throw new ArgumentNullException(nameof(replayGainService));
        _ffmpegService = ffmpegService ?? throw new ArgumentNullException(nameof(ffmpegService));
        _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));
        _logger = logger;

        NavigationItems.CollectionChanged += OnNavigationItemsCollectionChanged;
        PlayerButtons.CollectionChanged += OnPlayerButtonsCollectionChanged;
        LyricsProviders.CollectionChanged += OnLyricsProvidersCollectionChanged;
        MetadataProviders.CollectionChanged += OnMetadataProvidersCollectionChanged;
        _playbackService.EqualizerChanged += OnPlaybackService_EqualizerChanged;

#if MSIX_PACKAGE
        IsUpdateControlVisible = false;
#else
        IsUpdateControlVisible = true;
#endif
        
        AvailableEqualizerPresets = _playbackService.AvailablePresets.ToList();
        
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
        IsFetchOnlineLyricsEnabled = SettingsDefaults.FetchOnlineLyricsEnabled;
        IsDiscordRichPresenceEnabled = SettingsDefaults.DiscordRichPresenceEnabled;
        IsCheckForUpdatesEnabled = SettingsDefaults.CheckForUpdatesEnabled;
        IsRememberWindowSizeEnabled = SettingsDefaults.RememberWindowSizeEnabled;
        IsRememberWindowPositionEnabled = SettingsDefaults.RememberWindowPositionEnabled;
        IsRememberPaneStateEnabled = SettingsDefaults.RememberPaneStateEnabled;
        IsVolumeNormalizationEnabled = SettingsDefaults.VolumeNormalizationEnabled;
        EqualizerPreamp = SettingsDefaults.EqualizerPreamp;
        
        _isInitializing = false;
    }




    [ObservableProperty] public partial ElementTheme SelectedTheme { get; set; }
    [ObservableProperty] public partial BackdropMaterial SelectedBackdropMaterial { get; set; }
    [ObservableProperty] public partial bool IsDynamicThemingEnabled { get; set; }
    [ObservableProperty] public partial bool IsPlayerAnimationEnabled { get; set; }
    [ObservableProperty] public partial bool IsRestorePlaybackStateEnabled { get; set; }
    [ObservableProperty] public partial bool IsAutoLaunchEnabled { get; set; }
    [ObservableProperty] public partial bool IsStartMinimizedEnabled { get; set; }
    [ObservableProperty] public partial bool IsHideToTrayEnabled { get; set; }
    [ObservableProperty] public partial bool IsMinimizeToMiniPlayerEnabled { get; set; }
    [ObservableProperty] public partial bool IsShowCoverArtInTrayFlyoutEnabled { get; set; }
    [ObservableProperty] public partial bool IsFetchOnlineMetadataEnabled { get; set; }
    [ObservableProperty] public partial bool IsFetchOnlineLyricsEnabled { get; set; }
    [ObservableProperty] public partial bool IsDiscordRichPresenceEnabled { get; set; }
    [ObservableProperty] public partial bool IsCheckForUpdatesEnabled { get; set; }
    [ObservableProperty] public partial bool IsRememberWindowSizeEnabled { get; set; }
    [ObservableProperty] public partial bool IsRememberWindowPositionEnabled { get; set; }
    [ObservableProperty] public partial bool IsRememberPaneStateEnabled { get; set; }
    [ObservableProperty] public partial bool IsVolumeNormalizationEnabled { get; set; }
    [ObservableProperty] public partial float EqualizerPreamp { get; set; }
    [ObservableProperty] public partial Windows.UI.Color AccentColor { get; set; }

    [ObservableProperty] public partial EqualizerPreset? SelectedEqualizerPreset { get; set; }
    public List<EqualizerPreset> AvailableEqualizerPresets { get; private set; } = new();

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

    public ObservableCollection<EqualizerBandViewModel> EqualizerBands { get; } = new();
    public ObservableCollection<PlayerButtonSetting> PlayerButtons { get; } = new();
    public bool IsUpdateControlVisible { get; }
    public bool IsLastFmNotConnected => !IsLastFmConnected;
    public bool IsLastFmInitialAuthEnabled => !IsConnectingToLastFm;
    public ObservableCollection<NavigationItemSetting> NavigationItems { get; } = new();
    public ObservableCollection<ServiceProviderSettingViewModel> LyricsProviders { get; } = new();
    public ObservableCollection<ServiceProviderSettingViewModel> MetadataProviders { get; } = new();


    public List<ElementTheme> AvailableThemes { get; } = Enum.GetValues<ElementTheme>().ToList();
    public List<BackdropMaterial> AvailableBackdropMaterials { get; } = Enum.GetValues<BackdropMaterial>().ToList();
    public string ApplicationVersion => _appInfoService.GetAppVersion();

    public void Dispose()
    {
        if (_isDisposed) return;

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
        _preampDebounceCts?.Cancel();
        _preampDebounceCts?.Dispose();
        _replayGainScanCts?.Cancel();
        _replayGainScanCts?.Dispose();
        _playerButtonSaveCts?.Cancel();
        _playerButtonSaveCts?.Dispose();
        _navigationItemSaveCts?.Cancel();
        _navigationItemSaveCts?.Dispose();
        _lyricsProviderSaveCts?.Cancel();
        _lyricsProviderSaveCts?.Dispose();
        _metadataProviderSaveCts?.Cancel();
        _metadataProviderSaveCts?.Dispose();

        _loadLock.Dispose();
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    [RelayCommand]
    public async Task LoadSettingsAsync()
    {
        if (_isDisposed) return;
        await _loadLock.WaitAsync();
        try
        {
            _isInitializing = true;

            foreach (var item in NavigationItems) item.PropertyChanged -= OnNavigationItemPropertyChanged;
            NavigationItems.Clear();

            foreach (var item in PlayerButtons) item.PropertyChanged -= OnPlayerButtonPropertyChanged;
            PlayerButtons.Clear();

            foreach (var item in LyricsProviders) item.PropertyChanged -= OnLyricsProviderPropertyChanged;
            LyricsProviders.Clear();

            foreach (var item in MetadataProviders) item.PropertyChanged -= OnMetadataProviderPropertyChanged;
            MetadataProviders.Clear();

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
            var onlineLyricsTask = _settingsService.GetFetchOnlineLyricsEnabledAsync();
            var discordRpcTask = _settingsService.GetDiscordRichPresenceEnabledAsync();
            var checkUpdatesTask = _settingsService.GetCheckForUpdatesEnabledAsync();
            var rememberWindowTask = _settingsService.GetRememberWindowSizeEnabledAsync();
            var rememberPositionTask = _settingsService.GetRememberWindowPositionEnabledAsync();
            var rememberPaneTask = _settingsService.GetRememberPaneStateEnabledAsync();
            var volumeNormTask = _settingsService.GetVolumeNormalizationEnabledAsync();
            var lastFmCredsTask = _settingsService.GetLastFmCredentialsAsync();
            var lastFmAuthTokenTask = _settingsService.GetLastFmAuthTokenAsync();
            var scrobblingTask = _settingsService.GetLastFmScrobblingEnabledAsync();
            var nowPlayingTask = _settingsService.GetLastFmNowPlayingEnabledAsync();

            var accentColorTask = _settingsService.GetAccentColorAsync();

            var lyricsProvidersTask = _settingsService.GetServiceProvidersAsync(ServiceCategory.Lyrics);
            var metadataProvidersTask = _settingsService.GetServiceProvidersAsync(ServiceCategory.Metadata);

            await Task.WhenAll(
                navItemsTask, playerButtonsTask, themeTask, backdropTask, dynamicThemingTask,
                playerAnimationTask, restorePlaybackTask, autoLaunchTask, startMinimizedTask,
                hideToTrayTask, miniPlayerTask, trayFlyoutTask, onlineMetadataTask,
                onlineLyricsTask, discordRpcTask, checkUpdatesTask, rememberWindowTask,
                rememberPositionTask, rememberPaneTask, volumeNormTask, lastFmCredsTask, lastFmAuthTokenTask,
                scrobblingTask, nowPlayingTask, accentColorTask, lyricsProvidersTask, metadataProvidersTask);

            foreach (var item in navItemsTask.Result)
            {
                item.PropertyChanged += OnNavigationItemPropertyChanged;
                NavigationItems.Add(item);
            }

            foreach (var button in playerButtonsTask.Result)
            {
                button.PropertyChanged += OnPlayerButtonPropertyChanged;
                PlayerButtons.Add(button);
            }

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
            IsFetchOnlineLyricsEnabled = onlineLyricsTask.Result;
            IsDiscordRichPresenceEnabled = discordRpcTask.Result;
            IsCheckForUpdatesEnabled = checkUpdatesTask.Result;
            IsRememberWindowSizeEnabled = rememberWindowTask.Result;
            IsRememberWindowPositionEnabled = rememberPositionTask.Result;
            IsRememberPaneStateEnabled = rememberPaneTask.Result;
            IsVolumeNormalizationEnabled = volumeNormTask.Result;

            var lastFmCredentials = lastFmCredsTask.Result;
            LastFmUsername = lastFmCredentials?.Username;
            IsLastFmConnected = lastFmCredentials is not null && !string.IsNullOrEmpty(lastFmCredentials.Value.SessionKey);

            IsLastFmScrobblingEnabled = scrobblingTask.Result;
            IsLastFmNowPlayingEnabled = nowPlayingTask.Result;

            var authToken = lastFmAuthTokenTask.Result;
            IsConnectingToLastFm = !string.IsNullOrEmpty(authToken);

            var accentColor = accentColorTask.Result;
            if (accentColor != null)
            {
                AccentColor = accentColor.Value;
            }
            else
            {
                AccentColor = App.SystemAccentColor;
            }

            LoadEqualizerState();

            // Load service providers
            foreach (var provider in lyricsProvidersTask.Result)
            {
                LyricsProviders.Add(ServiceProviderSettingViewModel.FromSetting(provider));
            }

            var metadataProviders = metadataProvidersTask.Result
                .Select(ServiceProviderSettingViewModel.FromSetting)
                .ToList();

            // Ensure MusicBrainz is always first while preserving relative order of others
            var mbProvider = metadataProviders.FirstOrDefault(p => p.Id == ServiceProviderIds.MusicBrainz);
            if (mbProvider != null)
            {
                metadataProviders.Remove(mbProvider);
                metadataProviders.Insert(0, mbProvider);
            }

            foreach (var provider in metadataProviders)
            {
                MetadataProviders.Add(provider);
            }

            _isInitializing = false;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    [RelayCommand]
    private async Task ResetApplicationDataAsync()
    {
        var confirmed = await _uiService.ShowConfirmationDialogAsync(
            "Confirm Reset",
            "Are you sure you want to reset all application data and settings? This action cannot be undone. The application will return to the initial setup.",
            "Reset");

        if (!confirmed) return;

        try
        {
            await _applicationLifecycle.ResetAndNavigateToOnboardingAsync();
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Application reset failed");
            await _uiService.ShowMessageDialogAsync(
                "Reset Error",
                $"An error occurred while resetting application data: {ex.Message}. Please try restarting the app manually.");
        }
    }

    [RelayCommand]
    private async Task CheckForUpdatesManuallyAsync()
    {
        await _updateService.CheckForUpdatesManuallyAsync();
    }

    [RelayCommand]
    private async Task LastFmInitialAuthAsync()
    {
        IsConnectingToLastFm = true;
        var authData = await _lastFmAuthService.GetAuthenticationTokenAsync();
        if (authData is { Token: not null, AuthUrl: not null })
        {
            await _settingsService.SaveLastFmAuthTokenAsync(authData.Value.Token);
            await Launcher.LaunchUriAsync(new Uri(authData.Value.AuthUrl));
        }
        else
        {
            await _uiService.ShowMessageDialogAsync("Error", "Could not connect to Last.fm. Please try again later.");
            IsConnectingToLastFm = false;
        }
    }

    [RelayCommand]
    private async Task LastFmFinalizeAuthAsync()
    {
        var authToken = await _settingsService.GetLastFmAuthTokenAsync();
        if (string.IsNullOrEmpty(authToken)) return;

        var sessionData = await _lastFmAuthService.GetSessionAsync(authToken);
        if (sessionData is { Username: not null, SessionKey: not null })
        {
            await _settingsService.SaveLastFmCredentialsAsync(sessionData.Value.Username, sessionData.Value.SessionKey);
            LastFmUsername = sessionData.Value.Username;
            IsLastFmConnected = true;

            IsLastFmScrobblingEnabled = true;
            IsLastFmNowPlayingEnabled = true;
            await _settingsService.SetLastFmScrobblingEnabledAsync(true);
            await _settingsService.SetLastFmNowPlayingEnabledAsync(true);
        }
        else
        {
            await _uiService.ShowMessageDialogAsync("Authentication Failed",
                "Could not get a session from Last.fm. Please try connecting again.");
        }

        IsConnectingToLastFm = false;
        await _settingsService.SaveLastFmAuthTokenAsync(null);
    }

    [RelayCommand]
    private async Task CancelLastFmAuthAsync()
    {
        IsConnectingToLastFm = false;
        await _settingsService.SaveLastFmAuthTokenAsync(null);
    }

    [RelayCommand]
    private async Task LastFmDisconnectAsync()
    {
        var confirmed = await _uiService.ShowConfirmationDialogAsync(
            "Disconnect Last.fm",
            "Are you sure you want to disconnect your Last.fm account? Your scrobbling history will be preserved on Last.fm, but Nagi will no longer be able to scrobble.",
            "Disconnect");

        if (!confirmed) return;

        await _settingsService.ClearLastFmCredentialsAsync();
        await _settingsService.SaveLastFmAuthTokenAsync(null);

        IsLastFmConnected = false;
        LastFmUsername = null;
        IsConnectingToLastFm = false;
        IsLastFmScrobblingEnabled = false;
        IsLastFmNowPlayingEnabled = false;
    }

    [RelayCommand]
    private async Task ResetEqualizerAsync()
    {
        await _playbackService.ResetEqualizerAsync();
        // Resetting will result in all 0s, which matches "None".
        SelectedEqualizerPreset = AvailableEqualizerPresets.FirstOrDefault(p => p.Name == "None");
    }

    [RelayCommand]
    private async Task ResetPlayerButtonsAsync()
    {
        foreach (var item in PlayerButtons) item.PropertyChanged -= OnPlayerButtonPropertyChanged;
        PlayerButtons.Clear();

        var defaultButtons = _settingsService.GetDefaultPlayerButtonSettings();
        foreach (var button in defaultButtons)
        {
            button.PropertyChanged += OnPlayerButtonPropertyChanged;
            PlayerButtons.Add(button);
        }

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
            await _uiService.ShowMessageDialogAsync("Export Successful",
                $"Exported {result.PlaylistsExported} playlists ({result.TotalSongs} total songs) to:\n{folderPath}");
        }
        else
        {
            await _uiService.ShowMessageDialogAsync("Export Failed", result.ErrorMessage ?? "No playlists to export.");
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
            var message = $"Successfully imported {result.PlaylistsImported} playlists ({result.TotalMatchedSongs} songs).";
            if (result.TotalUnmatchedSongs > 0)
            {
                message += $"\n\n{result.TotalUnmatchedSongs} songs could not be found in your library.";
            }
            if (result.FailedFiles.Count > 0)
            {
                message += $"\n\n{result.FailedFiles.Count} files failed to import.";
            }
            await _uiService.ShowMessageDialogAsync("Import Successful", message);
        }
        else
        {
            await _uiService.ShowMessageDialogAsync("Import Failed", "No playlists could be imported.");
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
            var bandVm = new EqualizerBandViewModel(bandInfo.Index, bandInfo.Frequency, gain);
            bandVm.GainChanged += OnBandGainChanged;
            bandVm.GainCommitted += OnBandGainCommitted;
            EqualizerBands.Add(bandVm);
        }

        CheckIfCurrentSettingsMatchPreset();
    }

    private void OnBandGainCommitted(EqualizerBandViewModel sender, float gain)
    {
         if (_isInitializing) return;
         _playbackService.SetEqualizerBandAsync(sender.Index, gain);
    }

    private void OnBandGainChanged(EqualizerBandViewModel sender)
    {
        if (_isApplyingPreset) return;
        
        // If the user manually adjusted a valid "preset match", we might want to keep the preset selected?
        // No, user requested: "If a user selects another preset, it will overwrite what they currently have set."
        // And usually manual adjustment means "Custom" or deselecting current preset.
        // We'll check if the new state matches any preset (including the current one).
        // If it matches, select it. If not, set to null.
        CheckIfCurrentSettingsMatchPreset();
    }

    private void CheckIfCurrentSettingsMatchPreset()
    {
        if (_isApplyingPreset) return;

        // Create a temporary array of current values
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
        if (value == null || _isApplyingPreset) return;

        _isApplyingPreset = true;
        try
        {
            // Update Service First (Source of Truth)
            await _playbackService.SetEqualizerGainsAsync(value.Gains);

            // Then update local UI to match (suppressing external update logic)
            for (int i = 0; i < EqualizerBands.Count; i++)
            {
                if (i < value.Gains.Length)
                {
                    var band = EqualizerBands[i];
                    band.IsExternalUpdate = true;
                    band.Gain = value.Gains[i];
                    band.IsExternalUpdate = false;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying equalizer preset {PresetName}", value.Name);
        }
        finally
        {
            _isApplyingPreset = false;
        }
    }

    private void OnPlaybackService_EqualizerChanged()
    {
        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed) return;

            _isInitializing = true;
            var eqSettings = _playbackService.CurrentEqualizerSettings;
            if (eqSettings != null)
            {
                if (Math.Abs(EqualizerPreamp - eqSettings.Preamp) > float.Epsilon) EqualizerPreamp = eqSettings.Preamp;

                foreach (var bandVM in EqualizerBands)
                {
                    var newGain = eqSettings.BandGains.ElementAtOrDefault((int)bandVM.Index);
                    if (Math.Abs(bandVM.Gain - newGain) > float.Epsilon)
                    {
                        bandVM.IsExternalUpdate = true;
                        bandVM.Gain = newGain;
                        bandVM.IsExternalUpdate = false;
                    }
                }
            }

            CheckIfCurrentSettingsMatchPreset();

            _isInitializing = false;
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
        _playerButtonSaveCts?.Cancel();
        _playerButtonSaveCts?.Dispose();
        _playerButtonSaveCts = new CancellationTokenSource();
        var token = _playerButtonSaveCts.Token;

        // Capture snapshot on UI thread to avoid cross-thread access to ObservableCollection
        var snapshot = PlayerButtons.ToList();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SettingsSaveDebounceMs, token);
                await SavePlayerButtonSettingsAsync(snapshot);
            }
            catch (TaskCanceledException)
            {
                // Expected during rapid changes - debounce is working.
            }
        }, token);
    }

    private async Task SavePlayerButtonSettingsAsync(List<PlayerButtonSetting> settings)
    {
        try
        {
            await _settingsService.SetPlayerButtonSettingsAsync(settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save player button settings");
        }
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
        _navigationItemSaveCts?.Cancel();
        _navigationItemSaveCts?.Dispose();
        _navigationItemSaveCts = new CancellationTokenSource();
        var token = _navigationItemSaveCts.Token;

        var snapshot = NavigationItems.ToList();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SettingsSaveDebounceMs, token);
                await SaveNavigationItemsAsync(snapshot);
            }
            catch (TaskCanceledException)
            {
                // Expected during rapid changes.
            }
        }, token);
    }

    private async Task SaveNavigationItemsAsync(List<NavigationItemSetting> settings)
    {
        try
        {
            await _settingsService.SetNavigationItemsAsync(settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save navigation items");
        }
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
        _lyricsProviderSaveCts?.Cancel();
        _lyricsProviderSaveCts?.Dispose();
        _lyricsProviderSaveCts = new CancellationTokenSource();
        var token = _lyricsProviderSaveCts.Token;

        var snapshot = LyricsProviders.Select((vm, i) => vm.ToSetting(i)).ToList();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SettingsSaveDebounceMs, token);
                await _settingsService.SetServiceProvidersAsync(ServiceCategory.Lyrics, snapshot);
            }
            catch (TaskCanceledException)
            {
                // Expected during rapid changes.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save lyrics providers");
            }
        }, token);
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
            // Always use dispatcher to check and enforce MusicBrainz position
            // This avoids race conditions between the check and the fix
            _dispatcherService.TryEnqueue(() =>
            {
                // Check if MusicBrainz needs to be moved to first position
                var musicBrainzIndex = -1;
                for (var i = 0; i < MetadataProviders.Count; i++)
                {
                    if (MetadataProviders[i].Id == ServiceProviderIds.MusicBrainz)
                    {
                        musicBrainzIndex = i;
                        break;
                    }
                }

                if (musicBrainzIndex > 0)
                {
                    var mb = MetadataProviders[musicBrainzIndex];
                    _isInitializing = true;
                    MetadataProviders.RemoveAt(musicBrainzIndex);
                    MetadataProviders.Insert(0, mb);
                    _isInitializing = false;
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
        _metadataProviderSaveCts?.Cancel();
        _metadataProviderSaveCts?.Dispose();
        _metadataProviderSaveCts = new CancellationTokenSource();
        var token = _metadataProviderSaveCts.Token;

        var snapshot = MetadataProviders.Select((vm, i) => vm.ToSetting(i)).ToList();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SettingsSaveDebounceMs, token);
                await _settingsService.SetServiceProvidersAsync(ServiceCategory.Metadata, snapshot);
            }
            catch (TaskCanceledException)
            {
                // Expected during rapid changes.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save metadata providers");
            }
        }, token);
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

    partial void OnSelectedBackdropMaterialChanged(BackdropMaterial value)
    {
        if (_isInitializing) return;
        _ = _settingsService.SetBackdropMaterialAsync(value);
    }

    async partial void OnIsDynamicThemingEnabledChanged(bool value)
    {
        if (_isInitializing) return;
        try
        {
            await _settingsService.SetDynamicThemingAsync(value);
            if (value)
            {
                _ = _themeService.ReapplyCurrentDynamicThemeAsync();
            }
            else
            {
                var accentColor = await _settingsService.GetAccentColorAsync();
                _ = _themeService.ApplyAccentColorAsync(accentColor);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating dynamic theming to {Value}", value);
        }
    }

    partial void OnIsPlayerAnimationEnabledChanged(bool value)
    {
        if (_isInitializing) return;
        _ = _settingsService.SetPlayerAnimationEnabledAsync(value);
    }

    partial void OnIsRestorePlaybackStateEnabledChanged(bool value)
    {
        if (_isInitializing) return;
        _ = _settingsService.SetRestorePlaybackStateEnabledAsync(value);
    }

    partial void OnIsAutoLaunchEnabledChanged(bool value)
    {
        if (_isInitializing) return;
        _ = _settingsService.SetAutoLaunchEnabledAsync(value);
    }

    partial void OnIsStartMinimizedEnabledChanged(bool value)
    {
        if (_isInitializing) return;
        _ = _settingsService.SetStartMinimizedEnabledAsync(value);
    }

    partial void OnIsHideToTrayEnabledChanged(bool value)
    {
        if (_isInitializing) return;
        _ = _settingsService.SetHideToTrayEnabledAsync(value);
    }

    partial void OnIsMinimizeToMiniPlayerEnabledChanged(bool value)
    {
        if (_isInitializing) return;
        _ = _settingsService.SetMinimizeToMiniPlayerEnabledAsync(value);
    }

    partial void OnIsShowCoverArtInTrayFlyoutEnabledChanged(bool value)
    {
        if (_isInitializing) return;
        _ = _settingsService.SetShowCoverArtInTrayFlyoutAsync(value);
    }

    partial void OnIsFetchOnlineMetadataEnabledChanged(bool value)
    {
        if (_isInitializing) return;
        _ = _settingsService.SetFetchOnlineMetadataEnabledAsync(value);
    }

    partial void OnIsFetchOnlineLyricsEnabledChanged(bool value)
    {
        if (_isInitializing) return;
        _ = _settingsService.SetFetchOnlineLyricsEnabledAsync(value);
    }

    partial void OnIsDiscordRichPresenceEnabledChanged(bool value)
    {
        if (_isInitializing) return;
        _ = _settingsService.SetDiscordRichPresenceEnabledAsync(value);
    }

    partial void OnIsCheckForUpdatesEnabledChanged(bool value)
    {
        if (_isInitializing) return;
        _ = _settingsService.SetCheckForUpdatesEnabledAsync(value);
    }

    partial void OnIsRememberWindowSizeEnabledChanged(bool value)
    {
        if (_isInitializing) return;
        _ = _settingsService.SetRememberWindowSizeEnabledAsync(value);
    }

    partial void OnIsRememberWindowPositionEnabledChanged(bool value)
    {
        if (_isInitializing) return;
        _ = _settingsService.SetRememberWindowPositionEnabledAsync(value);
    }

    partial void OnIsRememberPaneStateEnabledChanged(bool value)
    {
        if (_isInitializing) return;
        _ = _settingsService.SetRememberPaneStateEnabledAsync(value);
    }

    async partial void OnIsVolumeNormalizationEnabledChanged(bool value)
    {
        if (_isInitializing) return;

        try
        {
            // When enabling, check for FFmpeg first
            if (value)
            {
                var isFFmpegInstalled = await _ffmpegService.IsFFmpegInstalledAsync();
                if (!isFFmpegInstalled)
                {
                    // Show dialog with recheck capability
                    var ffmpegDetected = await _uiService.ShowFFmpegSetupDialogAsync(
                        "FFmpeg Not Found",
                        _ffmpegService.GetFFmpegSetupInstructions(),
                        () => _ffmpegService.IsFFmpegInstalledAsync(forceRecheck: true));

                    if (!ffmpegDetected)
                    {
                        // User cancelled or FFmpeg still not found - revert the toggle
                        _isInitializing = true;
                        IsVolumeNormalizationEnabled = false;
                        _isInitializing = false;
                        return;
                    }
                }

                // If FFmpeg is present, show confirmation dialog and trigger scan
                var confirmed = await _uiService.ShowConfirmationDialogAsync(
                    "Enable Volume Normalization",
                    "Volume Normalization (ReplayGain) ensures a consistent volume level across all your music.\n\n" +
                    "This will analyze your music library and directly modify your audio files by writing ReplayGain tags. " +
                    "Only files without existing tags will be processed. This may take some time depending on your library size.\n\n" +
                    "Do you want to continue?",
                    "Enable & Scan",
                    "Cancel");

                if (!confirmed)
                {
                    // Revert the toggle without triggering this handler again
                    _isInitializing = true;
                    IsVolumeNormalizationEnabled = false;
                    _isInitializing = false;
                    return;
                }

                await _settingsService.SetVolumeNormalizationEnabledAsync(true);
                await ScanLibraryForReplayGainAsync();
            }
            else
            {
                // When disabling, just save the setting (no confirmation needed)
                await _settingsService.SetVolumeNormalizationEnabledAsync(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error toggling volume normalization to {Value}", value);
        }
    }

    /// <summary>
    ///     Scans the library for songs missing ReplayGain data and writes the tags.
    /// </summary>
    private async Task ScanLibraryForReplayGainAsync()
    {
        if (_playerViewModel.IsGlobalOperationInProgress) return;

        // Cancel any existing scan
        _replayGainScanCts?.Cancel();
        _replayGainScanCts?.Dispose();
        _replayGainScanCts = new CancellationTokenSource();
        var cancellationToken = _replayGainScanCts.Token;

        _playerViewModel.IsGlobalOperationInProgress = true;
        _playerViewModel.GlobalOperationStatusMessage = "Preparing volume normalization scan...";
        _playerViewModel.IsGlobalOperationIndeterminate = true;

        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                _playerViewModel.GlobalOperationStatusMessage = p.StatusText;
                _playerViewModel.IsGlobalOperationIndeterminate = p.IsIndeterminate || p.Percentage < 5;
                _playerViewModel.GlobalOperationProgressValue = p.Percentage;
            });

            await _replayGainService.ScanLibraryAsync(progress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _playerViewModel.GlobalOperationStatusMessage = "Volume normalization scan cancelled.";
        }
        catch (Exception ex)
        {
            _playerViewModel.GlobalOperationStatusMessage = $"Error during scan: {ex.Message}";
            _logger.LogError(ex, "Failed to scan library for ReplayGain");
        }
        finally
        {
            _playerViewModel.IsGlobalOperationInProgress = false;
            _playerViewModel.IsGlobalOperationIndeterminate = false;
        }
    }

    partial void OnIsLastFmScrobblingEnabledChanged(bool value)
    {
        if (_isInitializing) return;
        _ = _settingsService.SetLastFmScrobblingEnabledAsync(value);
    }

    partial void OnIsLastFmNowPlayingEnabledChanged(bool value)
    {
        if (_isInitializing) return;
        _ = _settingsService.SetLastFmNowPlayingEnabledAsync(value);
    }

    async partial void OnEqualizerPreampChanged(float value)
    {
        if (_isInitializing) return;

        _preampDebounceCts?.Cancel();
        _preampDebounceCts?.Dispose();

        _preampDebounceCts = new CancellationTokenSource();
        var token = _preampDebounceCts.Token;

        try
        {
            await Task.Delay(EqualizerDebounceDelayMilliseconds, token);
            await _playbackService.SetEqualizerPreampAsync(value);
        }
        catch (TaskCanceledException)
        {
            // Debounce was cancelled, which is expected behavior.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying equalizer preamp {Value}", value);
        }
    }
    async partial void OnAccentColorChanged(Windows.UI.Color value)
    {
        if (_isInitializing) return;
        try
        {
            await _settingsService.SetAccentColorAsync(value);
            if (!IsDynamicThemingEnabled)
            {
                _ = _themeService.ApplyAccentColorAsync(value);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating accent color to {Color}", value);
        }
    }

    [RelayCommand]
    private async Task ResetAccentColorAsync()
    {
        _isInitializing = true;
        AccentColor = App.SystemAccentColor;
        _isInitializing = false;
        
        await _settingsService.SetAccentColorAsync(null);
        if (!IsDynamicThemingEnabled)
        {
            _ = _themeService.ApplyAccentColorAsync(null);
        }
    }
}
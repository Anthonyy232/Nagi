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
using Nagi.WinUI.Models;
using Nagi.WinUI.Navigation;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     ViewModel for a single band in the audio equalizer.
/// </summary>
public partial class EqualizerBandViewModel : ObservableObject, IDisposable
{
    private const int KiloHertzThreshold = 1000;
    private const int DebounceDelayMilliseconds = 200;

    private readonly IMusicPlaybackService _playbackService;
    private CancellationTokenSource? _debounceCts;

    public EqualizerBandViewModel(uint index, float frequency, float initialGain, IMusicPlaybackService playbackService)
    {
        Index = index;
        FrequencyLabel = frequency < KiloHertzThreshold ? $"{frequency:F0}" : $"{frequency / KiloHertzThreshold:F0}K";
        Gain = initialGain;
        _playbackService = playbackService;
    }

    public uint Index { get; }
    public string FrequencyLabel { get; }

    [ObservableProperty] public partial float Gain { get; set; }

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
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();

        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        try
        {
            await Task.Delay(DebounceDelayMilliseconds, token);
            await _playbackService.SetEqualizerBandAsync(Index, value);
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
    private const int EqualizerDebounceDelayMilliseconds = 200;

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

    private bool _isDisposed;
    private bool _isInitializing;
    private CancellationTokenSource? _preampDebounceCts;

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
        _logger = logger;

        NavigationItems.CollectionChanged += OnNavigationItemsCollectionChanged;
        PlayerButtons.CollectionChanged += OnPlayerButtonsCollectionChanged;
        _playbackService.EqualizerChanged += OnPlaybackService_EqualizerChanged;

#if MSIX_PACKAGE
        IsUpdateControlVisible = false;
#else
        IsUpdateControlVisible = true;
#endif
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
    [ObservableProperty] public partial bool IsRememberPaneStateEnabled { get; set; }
    [ObservableProperty] public partial float EqualizerPreamp { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLastFmNotConnected))]
    public partial bool IsLastFmConnected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLastFmInitialAuthEnabled))]
    public partial bool IsConnectingToLastFm { get; set; }

    [ObservableProperty] public partial string? LastFmUsername { get; set; }
    [ObservableProperty] public partial bool IsLastFmScrobblingEnabled { get; set; }
    [ObservableProperty] public partial bool IsLastFmNowPlayingEnabled { get; set; }

    public ObservableCollection<EqualizerBandViewModel> EqualizerBands { get; } = new();
    public ObservableCollection<PlayerButtonSetting> PlayerButtons { get; } = new();
    public bool IsUpdateControlVisible { get; }
    public bool IsLastFmNotConnected => !IsLastFmConnected;
    public bool IsLastFmInitialAuthEnabled => !IsConnectingToLastFm;
    public ObservableCollection<NavigationItemSetting> NavigationItems { get; } = new();
    public List<ElementTheme> AvailableThemes { get; } = Enum.GetValues<ElementTheme>().ToList();
    public List<BackdropMaterial> AvailableBackdropMaterials { get; } = Enum.GetValues<BackdropMaterial>().ToList();
    public string ApplicationVersion => _appInfoService.GetAppVersion();

    public void Dispose()
    {
        if (_isDisposed) return;

        NavigationItems.CollectionChanged -= OnNavigationItemsCollectionChanged;
        PlayerButtons.CollectionChanged -= OnPlayerButtonsCollectionChanged;
        _playbackService.EqualizerChanged -= OnPlaybackService_EqualizerChanged;

        foreach (var item in NavigationItems) item.PropertyChanged -= OnNavigationItemPropertyChanged;

        foreach (var item in PlayerButtons) item.PropertyChanged -= OnPlayerButtonPropertyChanged;

        foreach (var bandVm in EqualizerBands) bandVm.Dispose();
        _preampDebounceCts?.Cancel();
        _preampDebounceCts?.Dispose();

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    [RelayCommand]
    public async Task LoadSettingsAsync()
    {
        _isInitializing = true;

        foreach (var item in NavigationItems) item.PropertyChanged -= OnNavigationItemPropertyChanged;
        NavigationItems.Clear();

        var navItems = await _settingsService.GetNavigationItemsAsync();
        foreach (var item in navItems)
        {
            item.PropertyChanged += OnNavigationItemPropertyChanged;
            NavigationItems.Add(item);
        }

        foreach (var item in PlayerButtons) item.PropertyChanged -= OnPlayerButtonPropertyChanged;
        PlayerButtons.Clear();
        var playerButtons = await _settingsService.GetPlayerButtonSettingsAsync();
        foreach (var button in playerButtons)
        {
            button.PropertyChanged += OnPlayerButtonPropertyChanged;
            PlayerButtons.Add(button);
        }

        SelectedTheme = await _settingsService.GetThemeAsync();
        SelectedBackdropMaterial = await _settingsService.GetBackdropMaterialAsync();
        IsDynamicThemingEnabled = await _settingsService.GetDynamicThemingAsync();
        IsPlayerAnimationEnabled = await _settingsService.GetPlayerAnimationEnabledAsync();
        IsRestorePlaybackStateEnabled = await _settingsService.GetRestorePlaybackStateEnabledAsync();
        IsAutoLaunchEnabled = await _settingsService.GetAutoLaunchEnabledAsync();
        IsStartMinimizedEnabled = await _settingsService.GetStartMinimizedEnabledAsync();
        IsHideToTrayEnabled = await _settingsService.GetHideToTrayEnabledAsync();
        IsMinimizeToMiniPlayerEnabled = await _settingsService.GetMinimizeToMiniPlayerEnabledAsync();
        IsShowCoverArtInTrayFlyoutEnabled = await _settingsService.GetShowCoverArtInTrayFlyoutAsync();
        IsFetchOnlineMetadataEnabled = await _settingsService.GetFetchOnlineMetadataEnabledAsync();
        IsFetchOnlineLyricsEnabled = await _settingsService.GetFetchOnlineLyricsEnabledAsync();
        IsDiscordRichPresenceEnabled = await _settingsService.GetDiscordRichPresenceEnabledAsync();
        IsCheckForUpdatesEnabled = await _settingsService.GetCheckForUpdatesEnabledAsync();
        IsRememberWindowSizeEnabled = await _settingsService.GetRememberWindowSizeEnabledAsync();
        IsRememberPaneStateEnabled = await _settingsService.GetRememberPaneStateEnabledAsync();
        
        var lastFmCredentials = await _settingsService.GetLastFmCredentialsAsync();
        LastFmUsername = lastFmCredentials?.Username;
        IsLastFmConnected = lastFmCredentials is not null && !string.IsNullOrEmpty(lastFmCredentials.Value.SessionKey);

        if (IsLastFmConnected)
        {
            IsLastFmScrobblingEnabled = await _settingsService.GetLastFmScrobblingEnabledAsync();
            IsLastFmNowPlayingEnabled = await _settingsService.GetLastFmNowPlayingEnabledAsync();
        }

        var authToken = await _settingsService.GetLastFmAuthTokenAsync();
        IsConnectingToLastFm = !string.IsNullOrEmpty(authToken);

        LoadEqualizerState();

        _isInitializing = false;
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
        var eqSettings = _playbackService.CurrentEqualizerSettings;
        if (eqSettings == null) return;

        EqualizerPreamp = eqSettings.Preamp;

        foreach (var bandVm in EqualizerBands) bandVm.Dispose();
        EqualizerBands.Clear();

        foreach (var bandInfo in _playbackService.EqualizerBands)
        {
            var gain = eqSettings.BandGains.ElementAtOrDefault((int)bandInfo.Index);
            EqualizerBands.Add(new EqualizerBandViewModel(bandInfo.Index, bandInfo.Frequency, gain, _playbackService));
        }
    }

    private void OnPlaybackService_EqualizerChanged()
    {
        _isInitializing = true;
        var eqSettings = _playbackService.CurrentEqualizerSettings;
        if (eqSettings != null)
        {
            if (Math.Abs(EqualizerPreamp - eqSettings.Preamp) > float.Epsilon) EqualizerPreamp = eqSettings.Preamp;

            foreach (var bandVM in EqualizerBands)
            {
                var newGain = eqSettings.BandGains.ElementAtOrDefault((int)bandVM.Index);
                if (Math.Abs(bandVM.Gain - newGain) > float.Epsilon) bandVM.Gain = newGain;
            }
        }

        _isInitializing = false;
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

        _ = SavePlayerButtonSettingsAsync();
    }

    private void OnPlayerButtonPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isInitializing || e.PropertyName != nameof(PlayerButtonSetting.IsEnabled)) return;
        _ = SavePlayerButtonSettingsAsync();
    }

    private async Task SavePlayerButtonSettingsAsync()
    {
        await _settingsService.SetPlayerButtonSettingsAsync(PlayerButtons.ToList());
    }

    private void OnNavigationItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        if (e.NewItems != null)
            foreach (NavigationItemSetting item in e.NewItems)
                item.PropertyChanged += OnNavigationItemPropertyChanged;

        if (e.OldItems != null)
            foreach (NavigationItemSetting item in e.OldItems)
                item.PropertyChanged -= OnNavigationItemPropertyChanged;

        _ = SaveNavigationItemsAsync();
    }

    private void OnNavigationItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isInitializing || e.PropertyName != nameof(NavigationItemSetting.IsEnabled)) return;
        _ = SaveNavigationItemsAsync();
    }

    private async Task SaveNavigationItemsAsync()
    {
        await _settingsService.SetNavigationItemsAsync(NavigationItems.ToList());
    }

    async partial void OnSelectedThemeChanged(ElementTheme value)
    {
        if (_isInitializing) return;
        await _settingsService.SetThemeAsync(value);
        _themeService.ApplyTheme(value);
    }

    partial void OnSelectedBackdropMaterialChanged(BackdropMaterial value)
    {
        if (_isInitializing) return;
        _ = _settingsService.SetBackdropMaterialAsync(value);
    }

    async partial void OnIsDynamicThemingEnabledChanged(bool value)
    {
        if (_isInitializing) return;
        await _settingsService.SetDynamicThemingAsync(value);
        _themeService.ReapplyCurrentDynamicTheme();
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

    async partial void OnIsRememberWindowSizeEnabledChanged(bool value)
    {
        if (_isInitializing) return;
        await _settingsService.SetRememberWindowSizeEnabledAsync(value);
        
        // When enabling, immediately save the current window size so the user's 
        // current window state is captured even if they close the app before resizing again.
        if (value && App.RootWindow is MainWindow mainWindow)
        {
            await mainWindow.SaveWindowSizeAsync();
        }
    }

    partial void OnIsRememberPaneStateEnabledChanged(bool value)
    {
        if (_isInitializing) return;
        _ = _settingsService.SetRememberPaneStateEnabledAsync(value);
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
    }
}
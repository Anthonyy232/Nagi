﻿using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Windows.System;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Services.Abstractions;
using Nagi.WinUI.Navigation;

namespace Nagi.WinUI.ViewModels;

/// <summary>
/// ViewModel for the Settings page, providing properties and commands to manage application settings.
/// </summary>
public partial class SettingsViewModel : ObservableObject, IDisposable {
    private readonly IUISettingsService _settingsService;
    private readonly IUIService _uiService;
    private readonly IThemeService _themeService;
    private readonly IApplicationLifecycle _applicationLifecycle;
    private readonly IAppInfoService _appInfoService;
    private readonly IUpdateService _updateService;
    private readonly ILastFmAuthService _lastFmAuthService;

    // Flag to prevent property change handlers from running during initial data loading.
    private bool _isInitializing;
    private bool _isDisposed;

    public SettingsViewModel(
        IUISettingsService settingsService,
        IUIService uiService,
        IThemeService themeService,
        IApplicationLifecycle applicationLifecycle,
        IAppInfoService appInfoService,
        IUpdateService updateService,
        ILastFmAuthService lastFmAuthService) {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _uiService = uiService ?? throw new ArgumentNullException(nameof(uiService));
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        _applicationLifecycle = applicationLifecycle ?? throw new ArgumentNullException(nameof(applicationLifecycle));
        _appInfoService = appInfoService ?? throw new ArgumentNullException(nameof(appInfoService));
        _updateService = updateService ?? throw new ArgumentNullException(nameof(updateService));
        _lastFmAuthService = lastFmAuthService ?? throw new ArgumentNullException(nameof(lastFmAuthService));

        NavigationItems.CollectionChanged += OnNavigationItemsCollectionChanged;

#if MSIX_PACKAGE
        IsUpdateControlVisible = false;
#else
        IsUpdateControlVisible = true;
#endif
    }

    [ObservableProperty]
    public partial ElementTheme SelectedTheme { get; set; }

    [ObservableProperty]
    public partial bool IsDynamicThemingEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsPlayerAnimationEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsShowLyricsOnPlayerEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsRestorePlaybackStateEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsAutoLaunchEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsStartMinimizedEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsHideToTrayEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsMinimizeToMiniPlayerEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsShowCoverArtInTrayFlyoutEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsFetchOnlineMetadataEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsDiscordRichPresenceEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsCheckForUpdatesEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLastFmNotConnected))]
    public partial bool IsLastFmConnected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLastFmInitialAuthEnabled))]
    public partial bool IsConnectingToLastFm { get; set; }

    [ObservableProperty]
    public partial string? LastFmUsername { get; set; }

    [ObservableProperty]
    public partial bool IsLastFmScrobblingEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsLastFmNowPlayingEnabled { get; set; }

    /// <summary>
    /// Gets a value indicating whether the update-related controls should be visible.
    /// </summary>
    public bool IsUpdateControlVisible { get; }

    /// <summary>
    /// Gets a value indicating whether the user is not connected to Last.fm.
    /// </summary>
    public bool IsLastFmNotConnected => !IsLastFmConnected;

    /// <summary>
    /// Gets a value indicating whether the initial Last.fm authentication button should be enabled.
    /// </summary>
    public bool IsLastFmInitialAuthEnabled => !IsConnectingToLastFm;

    /// <summary>
    /// Gets the collection of navigation items that can be configured by the user.
    /// </summary>
    public ObservableCollection<NavigationItemSetting> NavigationItems { get; } = new();

    /// <summary>
    /// Gets the list of available application themes.
    /// </summary>
    public List<ElementTheme> AvailableThemes { get; } = Enum.GetValues<ElementTheme>().ToList();

    /// <summary>
    /// Gets the current version of the application.
    /// </summary>
    public string ApplicationVersion => _appInfoService.GetAppVersion();

    /// <summary>
    /// Loads all settings from the settings service and populates the ViewModel properties.
    /// </summary>
    [RelayCommand]
    public async Task LoadSettingsAsync() {
        _isInitializing = true;

        foreach (var item in NavigationItems) {
            item.PropertyChanged -= OnNavigationItemPropertyChanged;
        }
        NavigationItems.Clear();

        var navItems = await _settingsService.GetNavigationItemsAsync();
        foreach (var item in navItems) {
            item.PropertyChanged += OnNavigationItemPropertyChanged;
            NavigationItems.Add(item);
        }

        SelectedTheme = await _settingsService.GetThemeAsync();
        IsDynamicThemingEnabled = await _settingsService.GetDynamicThemingAsync();
        IsPlayerAnimationEnabled = await _settingsService.GetPlayerAnimationEnabledAsync();
        IsShowLyricsOnPlayerEnabled = await _settingsService.GetShowLyricsOnPlayerEnabledAsync();
        IsRestorePlaybackStateEnabled = await _settingsService.GetRestorePlaybackStateEnabledAsync();
        IsAutoLaunchEnabled = await _settingsService.GetAutoLaunchEnabledAsync();
        IsStartMinimizedEnabled = await _settingsService.GetStartMinimizedEnabledAsync();
        IsHideToTrayEnabled = await _settingsService.GetHideToTrayEnabledAsync();
        IsMinimizeToMiniPlayerEnabled = await _settingsService.GetMinimizeToMiniPlayerEnabledAsync();
        IsShowCoverArtInTrayFlyoutEnabled = await _settingsService.GetShowCoverArtInTrayFlyoutAsync();
        IsFetchOnlineMetadataEnabled = await _settingsService.GetFetchOnlineMetadataEnabledAsync();
        IsDiscordRichPresenceEnabled = await _settingsService.GetDiscordRichPresenceEnabledAsync();
        IsCheckForUpdatesEnabled = await _settingsService.GetCheckForUpdatesEnabledAsync();

        var lastFmCredentials = await _settingsService.GetLastFmCredentialsAsync();
        LastFmUsername = lastFmCredentials?.Username;
        IsLastFmConnected = lastFmCredentials is not null && !string.IsNullOrEmpty(lastFmCredentials.Value.SessionKey);

        if (IsLastFmConnected) {
            IsLastFmScrobblingEnabled = await _settingsService.GetLastFmScrobblingEnabledAsync();
            IsLastFmNowPlayingEnabled = await _settingsService.GetLastFmNowPlayingEnabledAsync();
        }

        var authToken = await _settingsService.GetLastFmAuthTokenAsync();
        IsConnectingToLastFm = !string.IsNullOrEmpty(authToken);

        _isInitializing = false;
    }

    /// <summary>
    /// Prompts the user for confirmation and then resets all application data and settings to their defaults.
    /// </summary>
    [RelayCommand]
    private async Task ResetApplicationDataAsync() {
        bool confirmed = await _uiService.ShowConfirmationDialogAsync(
            "Confirm Reset",
            "Are you sure you want to reset all application data and settings? This action cannot be undone. The application will return to the initial setup.",
            "Reset",
            "Cancel");

        if (!confirmed) return;

        try {
            await _applicationLifecycle.ResetAndNavigateToOnboardingAsync();
        }
        catch (Exception ex) {
            Debug.WriteLine($"[CRITICAL] Application reset failed. Error: {ex.Message}\n{ex.StackTrace}");
            await _uiService.ShowMessageDialogAsync(
                "Reset Error",
                $"An error occurred while resetting application data: {ex.Message}. Please try restarting the app manually.");
        }
    }

    /// <summary>
    /// Manually triggers a check for application updates.
    /// </summary>
    [RelayCommand]
    private async Task CheckForUpdatesManuallyAsync() {
        await _updateService.CheckForUpdatesManuallyAsync();
    }

    /// <summary>
    /// Initiates the Last.fm connection process by opening the auth URL in the browser.
    /// </summary>
    [RelayCommand]
    private async Task LastFmInitialAuthAsync() {
        IsConnectingToLastFm = true;
        var authData = await _lastFmAuthService.GetAuthenticationTokenAsync();
        if (authData is { Token: not null, AuthUrl: not null }) {
            await _settingsService.SaveLastFmAuthTokenAsync(authData.Value.Token);
            await Launcher.LaunchUriAsync(new Uri(authData.Value.AuthUrl));
        }
        else {
            await _uiService.ShowMessageDialogAsync("Error", "Could not connect to Last.fm. Please try again later.");
            IsConnectingToLastFm = false;
        }
    }

    /// <summary>
    /// Finalizes the Last.fm connection after the user has authorized the app.
    /// </summary>
    [RelayCommand]
    private async Task LastFmFinalizeAuthAsync() {
        var authToken = await _settingsService.GetLastFmAuthTokenAsync();
        if (string.IsNullOrEmpty(authToken)) return;

        var sessionData = await _lastFmAuthService.GetSessionAsync(authToken);
        if (sessionData is { Username: not null, SessionKey: not null }) {
            await _settingsService.SaveLastFmCredentialsAsync(sessionData.Value.Username, sessionData.Value.SessionKey);
            LastFmUsername = sessionData.Value.Username;
            IsLastFmConnected = true;

            IsLastFmScrobblingEnabled = true;
            IsLastFmNowPlayingEnabled = true;
            await _settingsService.SetLastFmScrobblingEnabledAsync(true);
            await _settingsService.SetLastFmNowPlayingEnabledAsync(true);
        }
        else {
            await _uiService.ShowMessageDialogAsync("Authentication Failed", "Could not get a session from Last.fm. Please try connecting again.");
        }

        IsConnectingToLastFm = false;
        await _settingsService.SaveLastFmAuthTokenAsync(null);
    }

    /// <summary>
    /// Disconnects the user's Last.fm account by clearing saved credentials.
    /// </summary>
    [RelayCommand]
    private async Task LastFmDisconnectAsync() {
        bool confirmed = await _uiService.ShowConfirmationDialogAsync(
            "Disconnect Last.fm",
            "Are you sure you want to disconnect your Last.fm account? Your scrobbling history will be preserved on Last.fm, but Nagi will no longer be able to scrobble.",
            "Disconnect",
            "Cancel");

        if (!confirmed) return;

        await _settingsService.ClearLastFmCredentialsAsync();
        await _settingsService.SaveLastFmAuthTokenAsync(null);

        IsLastFmConnected = false;
        LastFmUsername = null;
        IsConnectingToLastFm = false;
        IsLastFmScrobblingEnabled = true;
        IsLastFmNowPlayingEnabled = true;
    }

    private void OnNavigationItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) {
        if (_isInitializing) return;

        if (e.NewItems != null) {
            foreach (NavigationItemSetting item in e.NewItems) {
                item.PropertyChanged += OnNavigationItemPropertyChanged;
            }
        }

        if (e.OldItems != null) {
            foreach (NavigationItemSetting item in e.OldItems) {
                item.PropertyChanged -= OnNavigationItemPropertyChanged;
            }
        }

        _ = SaveNavigationItemsAsync();
    }

    private void OnNavigationItemPropertyChanged(object? sender, PropertyChangedEventArgs e) {
        if (_isInitializing || e.PropertyName != nameof(NavigationItemSetting.IsEnabled)) return;
        _ = SaveNavigationItemsAsync();
    }

    private async Task SaveNavigationItemsAsync() {
        await _settingsService.SetNavigationItemsAsync(NavigationItems.ToList());
    }

    partial void OnSelectedThemeChanged(ElementTheme value) {
        if (_isInitializing) return;
        _ = ApplyAndSaveThemeAsync(value);
    }

    private async Task ApplyAndSaveThemeAsync(ElementTheme theme) {
        await _settingsService.SetThemeAsync(theme);
        _themeService.ApplyTheme(theme);
    }

    partial void OnIsDynamicThemingEnabledChanged(bool value) {
        if (_isInitializing) return;
        _ = ApplyAndSaveDynamicThemingAsync(value);
    }

    private async Task ApplyAndSaveDynamicThemingAsync(bool isEnabled) {
        await _settingsService.SetDynamicThemingAsync(isEnabled);
        _themeService.ReapplyCurrentDynamicTheme();
    }

    partial void OnIsPlayerAnimationEnabledChanged(bool value) {
        if (_isInitializing) return;
        _ = _settingsService.SetPlayerAnimationEnabledAsync(value);
    }

    partial void OnIsShowLyricsOnPlayerEnabledChanged(bool value) {
        if (_isInitializing) return;
        _ = _settingsService.SetShowLyricsOnPlayerEnabledAsync(value);
    }

    partial void OnIsRestorePlaybackStateEnabledChanged(bool value) {
        if (_isInitializing) return;
        _ = _settingsService.SetRestorePlaybackStateEnabledAsync(value);
    }

    partial void OnIsAutoLaunchEnabledChanged(bool value) {
        if (_isInitializing) return;
        _ = _settingsService.SetAutoLaunchEnabledAsync(value);
    }

    partial void OnIsStartMinimizedEnabledChanged(bool value) {
        if (_isInitializing) return;
        _ = _settingsService.SetStartMinimizedEnabledAsync(value);
    }

    partial void OnIsHideToTrayEnabledChanged(bool value) {
        if (_isInitializing) return;
        _ = _settingsService.SetHideToTrayEnabledAsync(value);
    }

    partial void OnIsMinimizeToMiniPlayerEnabledChanged(bool value) {
        if (_isInitializing) return;
        _ = _settingsService.SetMinimizeToMiniPlayerEnabledAsync(value);
    }

    partial void OnIsShowCoverArtInTrayFlyoutEnabledChanged(bool value) {
        if (_isInitializing) return;
        _ = _settingsService.SetShowCoverArtInTrayFlyoutAsync(value);
    }

    partial void OnIsFetchOnlineMetadataEnabledChanged(bool value) {
        if (_isInitializing) return;
        _ = _settingsService.SetFetchOnlineMetadataEnabledAsync(value);
    }

    partial void OnIsDiscordRichPresenceEnabledChanged(bool value) {
        if (_isInitializing) return;
        _ = _settingsService.SetDiscordRichPresenceEnabledAsync(value);
    }

    partial void OnIsCheckForUpdatesEnabledChanged(bool value) {
        if (_isInitializing) return;
        _ = _settingsService.SetCheckForUpdatesEnabledAsync(value);
    }

    partial void OnIsLastFmScrobblingEnabledChanged(bool value) {
        if (_isInitializing) return;
        _ = _settingsService.SetLastFmScrobblingEnabledAsync(value);
    }

    partial void OnIsLastFmNowPlayingEnabledChanged(bool value) {
        if (_isInitializing) return;
        _ = _settingsService.SetLastFmNowPlayingEnabledAsync(value);
    }

    /// <summary>
    /// Cleans up resources by unsubscribing from event handlers to prevent memory leaks.
    /// </summary>
    public void Dispose() {
        if (_isDisposed) return;

        // Unsubscribe from the collection itself
        NavigationItems.CollectionChanged -= OnNavigationItemsCollectionChanged;

        // Unsubscribe from each item within the collection
        foreach (var item in NavigationItems) {
            item.PropertyChanged -= OnNavigationItemPropertyChanged;
        }

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}
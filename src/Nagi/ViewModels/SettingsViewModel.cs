using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Nagi.Models;
using Nagi.Services.Abstractions;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Nagi.ViewModels;

/// <summary>
/// ViewModel for the Settings page, providing properties and commands to manage application settings.
/// </summary>
public partial class SettingsViewModel : ObservableObject {
    private readonly ISettingsService _settingsService;
    private readonly IUIService _uiService;
    private readonly IThemeService _themeService;
    private readonly IApplicationLifecycle _applicationLifecycle;
    private readonly IAppInfoService _appInfoService;
    private readonly IUpdateService _updateService;

    // Flag to prevent property change handlers from running during initial data loading.
    private bool _isInitializing;

    public SettingsViewModel(
        ISettingsService settingsService,
        IUIService uiService,
        IThemeService themeService,
        IApplicationLifecycle applicationLifecycle,
        IAppInfoService appInfoService,
        IUpdateService updateService) {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _uiService = uiService ?? throw new ArgumentNullException(nameof(uiService));
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        _applicationLifecycle = applicationLifecycle ?? throw new ArgumentNullException(nameof(applicationLifecycle));
        _appInfoService = appInfoService ?? throw new ArgumentNullException(nameof(appInfoService));
        _updateService = updateService ?? throw new ArgumentNullException(nameof(updateService));
        NavigationItems.CollectionChanged += OnNavigationItemsCollectionChanged;
    }

    [ObservableProperty]
    private ElementTheme _selectedTheme;

    [ObservableProperty]
    private bool _isDynamicThemingEnabled;

    [ObservableProperty]
    private bool _isPlayerAnimationEnabled;

    [ObservableProperty]
    private bool _isRestorePlaybackStateEnabled;

    [ObservableProperty]
    private bool _isAutoLaunchEnabled;

    [ObservableProperty]
    private bool _isStartMinimizedEnabled;

    [ObservableProperty]
    private bool _isHideToTrayEnabled;

    [ObservableProperty]
    private bool _isShowCoverArtInTrayFlyoutEnabled;

    [ObservableProperty]
    private bool _isFetchOnlineMetadataEnabled;

    [ObservableProperty]
    private bool _isCheckForUpdatesEnabled;

    public ObservableCollection<NavigationItemSetting> NavigationItems { get; } = new();
    public List<ElementTheme> AvailableThemes { get; } = Enum.GetValues<ElementTheme>().ToList();
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
        IsRestorePlaybackStateEnabled = await _settingsService.GetRestorePlaybackStateEnabledAsync();
        IsAutoLaunchEnabled = await _settingsService.GetAutoLaunchEnabledAsync();
        IsStartMinimizedEnabled = await _settingsService.GetStartMinimizedEnabledAsync();
        IsHideToTrayEnabled = await _settingsService.GetHideToTrayEnabledAsync();
        IsShowCoverArtInTrayFlyoutEnabled = await _settingsService.GetShowCoverArtInTrayFlyoutAsync();
        IsFetchOnlineMetadataEnabled = await _settingsService.GetFetchOnlineMetadataEnabledAsync();
        IsCheckForUpdatesEnabled = await _settingsService.GetCheckForUpdatesEnabledAsync();

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

    partial void OnIsShowCoverArtInTrayFlyoutEnabledChanged(bool value) {
        if (_isInitializing) return;
        _ = _settingsService.SetShowCoverArtInTrayFlyoutAsync(value);
    }

    partial void OnIsFetchOnlineMetadataEnabledChanged(bool value) {
        if (_isInitializing) return;
        _ = _settingsService.SetFetchOnlineMetadataEnabledAsync(value);
    }

    partial void OnIsCheckForUpdatesEnabledChanged(bool value) {
        if (_isInitializing) return;
        _ = _settingsService.SetCheckForUpdatesEnabledAsync(value);
    }
}
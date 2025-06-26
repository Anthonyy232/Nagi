using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.Services.Abstractions;

namespace Nagi.ViewModels;

/// <summary>
/// Provides properties and commands for the settings page, allowing users to configure the application.
/// </summary>
public partial class SettingsViewModel : ObservableObject {
    private readonly IServiceProvider _serviceProvider;
    private readonly ISettingsService _settingsService;

    private bool _isInitializing;

    public SettingsViewModel(ISettingsService settingsService, IServiceProvider serviceProvider) {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    /// <summary>
    /// Gets or sets the selected application theme (Light, Dark, or System Default).
    /// </summary>
    [ObservableProperty]
    private ElementTheme _selectedTheme;

    /// <summary>
    /// Gets or sets a value indicating whether dynamic theming based on album art is enabled.
    /// </summary>
    [ObservableProperty]
    private bool _isDynamicThemingEnabled;

    /// <summary>
    /// Gets or sets a value indicating whether animations for the player bar are enabled.
    /// </summary>
    [ObservableProperty]
    private bool _isPlayerAnimationEnabled;

    /// <summary>
    /// Gets or sets a value indicating whether the application should launch automatically on system startup.
    /// </summary>
    [ObservableProperty]
    private bool _isAutoLaunchEnabled;

    /// <summary>
    /// Gets or sets a value indicating whether the application should start in a minimized state.
    /// </summary>
    [ObservableProperty]
    private bool _isStartMinimizedEnabled;

    /// <summary>
    /// Gets or sets a value indicating whether the application should hide to the system tray when closed.
    /// </summary>
    [ObservableProperty]
    private bool _isHideToTrayEnabled;

    /// <summary>
    /// Gets the list of available themes for binding to the UI.
    /// </summary>
    public List<ElementTheme> AvailableThemes { get; } =
        Enum.GetValues(typeof(ElementTheme)).Cast<ElementTheme>().ToList();

    /// <summary>
    /// Asynchronously loads all settings from the settings service and populates the ViewModel properties.
    /// </summary>
    [RelayCommand]
    public async Task LoadSettingsAsync() {
        _isInitializing = true;
        SelectedTheme = await _settingsService.GetThemeAsync();
        IsDynamicThemingEnabled = await _settingsService.GetDynamicThemingAsync();
        IsPlayerAnimationEnabled = await _settingsService.GetPlayerAnimationEnabledAsync();
        IsAutoLaunchEnabled = await _settingsService.GetAutoLaunchEnabledAsync();
        IsStartMinimizedEnabled = await _settingsService.GetStartMinimizedEnabledAsync();
        IsHideToTrayEnabled = await _settingsService.GetHideToTrayEnabledAsync();
        _isInitializing = false;
    }

    partial void OnSelectedThemeChanged(ElementTheme value) {
        if (_isInitializing) return;
        _ = ApplyAndSaveThemeAsync(value);
    }

    private async Task ApplyAndSaveThemeAsync(ElementTheme theme) {
        await _settingsService.SetThemeAsync(theme);
        if (Application.Current is App appInstance) appInstance.ApplyTheme(theme);
    }

    partial void OnIsDynamicThemingEnabledChanged(bool value) {
        if (_isInitializing) return;
        _ = ApplyAndSaveDynamicThemingAsync(value);
    }

    private async Task ApplyAndSaveDynamicThemingAsync(bool isEnabled) {
        await _settingsService.SetDynamicThemingAsync(isEnabled);
        if (Application.Current is App appInstance) {
            appInstance.ReapplyCurrentDynamicTheme();
        }
    }

    partial void OnIsPlayerAnimationEnabledChanged(bool value) {
        if (_isInitializing) return;
        _ = _settingsService.SetPlayerAnimationEnabledAsync(value);
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

    /// <summary>
    /// Resets all application data, including settings and the music library, to their default states
    /// after user confirmation.
    /// </summary>
    [RelayCommand]
    private async Task ResetApplicationDataAsync() {
        if (App.RootWindow?.Content?.XamlRoot is not { } xamlRoot) return;

        var confirmDialog = new ContentDialog {
            Title = "Confirm Reset",
            Content = "Are you sure you want to reset all application data and settings? This action cannot be undone. The application will return to the initial setup.",
            PrimaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            XamlRoot = xamlRoot
        };

        var result = await confirmDialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        try {
            await _settingsService.ResetToDefaultsAsync();

            var libraryService = _serviceProvider.GetRequiredService<ILibraryService>();
            await libraryService.ClearAllLibraryDataAsync();

            var playbackService = _serviceProvider.GetRequiredService<IMusicPlaybackService>();
            await playbackService.ClearQueueAsync();

            if (App.CurrentApp is App appInstance) await appInstance.CheckAndNavigateToMainContent();
        }
        catch (Exception ex) {
            Debug.WriteLine($"CRITICAL: Application reset failed. Error: {ex.Message}\n{ex.StackTrace}");

            if (xamlRoot.IsHostVisible) {
                var errorDialog = new ContentDialog {
                    Title = "Reset Error",
                    Content = $"An error occurred while resetting application data: {ex.Message}. Please try restarting the app manually.",
                    CloseButtonText = "OK",
                    XamlRoot = xamlRoot
                };
                await errorDialog.ShowAsync();
            }
        }
    }
}
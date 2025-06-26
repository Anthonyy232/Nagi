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
using Nagi.Services;
using Nagi.Services.Abstractions;

namespace Nagi.ViewModels;

/// <summary>
///     ViewModel for managing application settings.
/// </summary>
public partial class SettingsViewModel : ObservableObject {
    private readonly IServiceProvider _serviceProvider;
    private readonly ISettingsService _settingsService;

    // Prevents settings from being saved while they are initially being loaded.
    private bool _isInitializing;

    public SettingsViewModel(ISettingsService settingsService, IServiceProvider serviceProvider) {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    [ObservableProperty] public partial ElementTheme SelectedTheme { get; set; }

    [ObservableProperty] public partial bool IsDynamicThemingEnabled { get; set; }

    [ObservableProperty] public partial bool IsPlayerAnimationEnabled { get; set; }

    [ObservableProperty] public partial bool IsAutoLaunchEnabled { get; set; }

    [ObservableProperty] public partial bool IsStartMinimizedEnabled { get; set; }

    [ObservableProperty] public partial bool IsHideToTrayEnabled { get; set; }

    /// <summary>
    ///     Gets the list of available themes for the application.
    /// </summary>
    public List<ElementTheme> AvailableThemes { get; } =
        Enum.GetValues(typeof(ElementTheme)).Cast<ElementTheme>().ToList();

    /// <summary>
    ///     Asynchronously loads settings from the settings service.
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
        // Apply and persist the new theme setting without awaiting completion.
        _ = ApplyAndSaveThemeAsync(value);
    }

    private async Task ApplyAndSaveThemeAsync(ElementTheme theme) {
        await _settingsService.SetThemeAsync(theme);
        if (Application.Current is App appInstance) appInstance.ApplyTheme(theme);
    }

    partial void OnIsDynamicThemingEnabledChanged(bool value) {
        if (_isInitializing) return;
        // Apply and persist the new dynamic theming setting without awaiting completion.
        _ = ApplyAndSaveDynamicThemingAsync(value);
    }

    private async Task ApplyAndSaveDynamicThemingAsync(bool isEnabled) {
        await _settingsService.SetDynamicThemingAsync(isEnabled);
        if (Application.Current is App appInstance)
            // Re-evaluate the dynamic theme based on the current playback state and the new setting.
            appInstance.ReapplyCurrentDynamicTheme();
    }

    partial void OnIsPlayerAnimationEnabledChanged(bool value) {
        if (_isInitializing) return;
        // Persist the new player animation setting.
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
    ///     Resets all application data and settings to their defaults.
    ///     The application will clear all data and navigate to the initial setup view.
    /// </summary>
    [RelayCommand]
    private async Task ResetApplicationDataAsync() {
        if (App.RootWindow?.Content?.XamlRoot is not XamlRoot xamlRoot)
            // Cannot show a dialog without a XamlRoot. Abort the operation.
            return;

        var confirmDialog = new ContentDialog {
            Title = "Confirm Reset",
            Content =
                "Are you sure you want to reset all application data and settings? This action cannot be undone. The application will return to the initial setup.",
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
            // A critical error occurred during the reset process. Log it and attempt to inform the user.
            Debug.WriteLine($"CRITICAL: Application reset failed. Error: {ex.Message}\n{ex.StackTrace}");

            if (xamlRoot.IsHostVisible) {
                var errorDialog = new ContentDialog {
                    Title = "Reset Error",
                    Content =
                        $"An error occurred while resetting application data: {ex.Message}. Please try restarting the app manually.",
                    CloseButtonText = "OK",
                    XamlRoot = xamlRoot
                };
                // We can't guarantee this dialog will show if the app is in a bad state, but we should try.
                await errorDialog.ShowAsync();
            }
        }
    }
}
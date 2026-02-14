using System;
using Windows.System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Nagi.WinUI.Controls;
using Nagi.WinUI.Pages;
using Nagi.WinUI.Resources;
using Nagi.WinUI.Services.Abstractions;
using Nagi.WinUI.ViewModels;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Models;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;

namespace Nagi.WinUI;

/// <summary>
///     The main shell of the application, hosting navigation, content, and the media player.
///     This page also provides the custom title bar elements to the main window.
/// </summary>
public sealed partial class MainPage : UserControl, ICustomTitleBarProvider
{
    private const double VolumeChangeStep = 5.0;

    // Maps detail pages back to their parent navigation item for selection synchronization.
    private readonly Dictionary<Type, string> _detailPageToParentTagMap = new()
    {
        { typeof(PlaylistSongViewPage), "Playlists" },
        { typeof(SmartPlaylistSongViewPage), "Playlists" },
        { typeof(FolderSongViewPage), "Folders" },
        { typeof(ArtistViewPage), "Artists" },
        { typeof(AlbumViewPage), "Albums" },
        { typeof(GenreViewPage), "Genres" }
    };

    private readonly IDispatcherService _dispatcherService;
    private readonly ILogger<MainPage> _logger;

    // Maps navigation item tags to their corresponding page types.
    private readonly Dictionary<string, Type> _pages = new()
    {
        { "Library", typeof(LibraryPage) },
        { "Folders", typeof(FolderPage) },
        { "Playlists", typeof(PlaylistPage) },
        { "Settings", typeof(SettingsPage) },
        { "Artists", typeof(ArtistPage) },
        { "Albums", typeof(AlbumPage) },
        { "Genres", typeof(GenrePage) }
    };

    private readonly IUISettingsService _settingsService;
    private readonly IThemeService _themeService;
    private readonly IWin32InteropService _win32InteropService;

    // A flag to control the player's expand/collapse animation based on user settings.
    private bool _isPlayerAnimationEnabled = true;

    // A flag to track if the pointer is currently over the floating player control.
    private bool _isPointerOverPlayer;

    // A flag to track if the queue flyout is open, to keep the player expanded.
    private bool _isQueueFlyoutOpen;

    // A flag to prevent re-entrant navigation while the selection is being updated programmatically.
    private bool _isUpdatingNavViewSelection;

    // A flag to track if the page has been unloaded, to prevent dispatcher callbacks from updating UI.
    private bool _isUnloaded;

    private ElementTheme _lastKnownTheme;

    public MainPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<PlayerViewModel>();
        _settingsService = App.Services!.GetRequiredService<IUISettingsService>();
        _themeService = App.Services!.GetRequiredService<IThemeService>();
        _dispatcherService = App.Services!.GetRequiredService<IDispatcherService>();
        _win32InteropService = App.Services!.GetRequiredService<IWin32InteropService>();
        _logger = App.Services!.GetRequiredService<ILogger<MainPage>>();
        DataContext = ViewModel;

        InitializeNavigationService();

        Loaded += OnMainPageLoaded;
        Unloaded += OnMainPageUnloaded;
    }

    public PlayerViewModel ViewModel { get; }

    public TitleBar GetAppTitleBarElement()
    {
        return AppTitleBar;
    }

    public RowDefinition GetAppTitleBarRowElement()
    {
        return AppTitleBarRow;
    }

    // Initializes the navigation service with the content frame.
    private void InitializeNavigationService()
    {
        var navigationService = App.Services!.GetRequiredService<INavigationService>();
        navigationService.Initialize(ContentFrame);
    }

    /// <summary>
    ///     Updates the visual state of the title bar based on window activation.
    /// </summary>
    /// <param name="activationState">The current activation state of the window.</param>
    public void UpdateActivationVisualState(WindowActivationState activationState)
    {
        var stateName = activationState == WindowActivationState.Deactivated
            ? "WindowIsInactive"
            : "WindowIsActive";
        VisualStateManager.GoToState(this, stateName, true);
    }

    // Handles navigation logic when a NavigationView item is invoked or selected.
    private void HandleNavigation(bool isSettings, object? selectedItem, NavigationTransitionInfo transitionInfo)
    {
        if (isSettings)
        {
            if (ContentFrame.CurrentSourcePageType != typeof(SettingsPage))
                ContentFrame.Navigate(typeof(SettingsPage), null, transitionInfo);
            return;
        }

        if (selectedItem is NavigationViewItem { Tag: string tag } &&
            _pages.TryGetValue(tag, out var pageType) &&
            ContentFrame.CurrentSourcePageType != pageType) ContentFrame.Navigate(pageType, null, transitionInfo);
    }

    // Navigates back if possible.
    private void TryGoBack()
    {
        try
        {
            if (ContentFrame.CanGoBack) ContentFrame.GoBack();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to navigate back.");
        }
    }

    // Synchronizes the NavigationView's selected item with the currently displayed page.
    private void UpdateNavViewSelection(Type currentPageType)
    {
        _isUpdatingNavViewSelection = true;

        if (currentPageType == typeof(SettingsPage))
        {
            NavView.SelectedItem = NavView.SettingsItem;
        }
        else
        {
            var tagToSelect = _pages.FirstOrDefault(p => p.Value == currentPageType).Key;
            if (tagToSelect is null) _detailPageToParentTagMap.TryGetValue(currentPageType, out tagToSelect);

            if (tagToSelect != null)
                NavView.SelectedItem = NavView.MenuItems
                    .OfType<NavigationViewItem>()
                    .FirstOrDefault(menuItem => menuItem.Tag?.ToString() == tagToSelect);
            else
                NavView.SelectedItem = null;
        }

        _isUpdatingNavViewSelection = false;
    }

    // Applies a dynamic theme based on the currently playing track's album art.
    private Task ApplyDynamicThemeForCurrentTrackAsync()
    {
        var song = ViewModel.CurrentPlayingTrack;
        return _themeService.ApplyDynamicThemeFromSwatchesAsync(song?.LightSwatchId, song?.DarkSwatchId);
    }

    // Updates the visual state of the floating player (expanded or collapsed).
    private void UpdatePlayerVisualState(bool useTransitions = true)
    {
        var isPlaying = ViewModel.CurrentPlayingTrack != null && ViewModel.IsPlaying;
        var shouldBeExpanded = !_isPlayerAnimationEnabled || isPlaying || _isPointerOverPlayer || _isQueueFlyoutOpen;
        var stateName = shouldBeExpanded ? "PlayerExpanded" : "PlayerCollapsed";

        // XAML handles MinHeight/MaxHeight (layout-dependent)
        VisualStateManager.GoToState(this, stateName, useTransitions);

        // Composition handles opacity (GPU-accelerated)
        if (useTransitions)
        {
            var targetOpacity = shouldBeExpanded ? 1f : 0f;
            var duration = shouldBeExpanded ? 350 : 150; // 350ms matches XAML SeekBar timing
            var delay = shouldBeExpanded ? 100 : 0;

            AnimateOpacity(SeekBarGrid, targetOpacity, duration, delay);
            AnimateOpacity(ArtistNameHyperlink, targetOpacity, duration, delay);
            AnimateOpacity(SecondaryControlsContainer, targetOpacity, duration, delay);
        }
        else
        {
            // Instant state change without animation
            var targetOpacity = shouldBeExpanded ? 1f : 0f;
            SetOpacityImmediate(SeekBarGrid, targetOpacity);
            SetOpacityImmediate(ArtistNameHyperlink, targetOpacity);
            SetOpacityImmediate(SecondaryControlsContainer, targetOpacity);
        }
    }

    /// <summary>
    ///     Animates an element's opacity using GPU-accelerated Composition animations.
    /// </summary>
    private static void AnimateOpacity(UIElement element, float to, int durationMs, int delayMs = 0)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;

        // Stop any running opacity animation to prevent overlap
        visual.StopAnimation("Opacity");

        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1.0f, to, compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.25f, 0.1f),
            new Vector2(0.25f, 1.0f)));
        animation.Duration = TimeSpan.FromMilliseconds(durationMs);
        animation.DelayTime = TimeSpan.FromMilliseconds(delayMs);

        visual.StartAnimation("Opacity", animation);
    }

    /// <summary>
    ///     Sets an element's opacity immediately without animation.
    /// </summary>
    private static void SetOpacityImmediate(UIElement element, float opacity)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.Opacity = opacity;
    }

    // Populates the NavigationView with items based on user settings.
    private async Task PopulateNavigationAsync()
    {
        NavView.MenuItems.Clear();
        var navItems = await _settingsService.GetNavigationItemsAsync();

        foreach (var item in navItems.Where(i => i.IsEnabled))
        {
            // Normalize the tag to match Resource keys (PascalCase) even if older settings used lowercase.
            var tag = item.Tag;
            if (tag.Length > 0 && char.IsLower(tag[0]))
            {
                tag = char.ToUpper(tag[0]) + tag.Substring(1);
            }

            var navViewItem = new NavigationViewItem
            {
                Content = Strings.GetString($"NavItem_{tag}"),
                Tag = tag,
                Icon = new FontIcon { Glyph = item.IconGlyph }
            };

            if (!string.IsNullOrEmpty(item.IconFontFamily))
                if (navViewItem.Icon is FontIcon icon)
                    icon.FontFamily = new FontFamily(item.IconFontFamily);

            NavView.MenuItems.Add(navViewItem);
        }
    }

    // Sets up event handlers and initial state when the page is loaded.
    private async void OnMainPageLoaded(object sender, RoutedEventArgs e)
    {
        if (NavView.SettingsItem is NavigationViewItem settingsItem)
        {
            settingsItem.Content = Strings.NavItem_Settings;
        }

        try
        {
            // 1. Hook up event handlers first to avoid missing any updates during the async initialization phase.
            _lastKnownTheme = ActualTheme;
            ActualThemeChanged += OnActualThemeChanged;
            ContentFrame.Navigated += OnContentFrameNavigated;
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            _settingsService.PlayerAnimationSettingChanged += OnPlayerAnimationSettingChanged;
            _settingsService.NavigationSettingsChanged += OnNavigationSettingsChanged;
            _settingsService.TransparencyEffectsSettingChanged += OnTransparencyEffectsSettingChanged;
            _settingsService.PlayerDesignSettingsChanged += OnPlayerDesignSettingsChanged;
            AddHandler(PointerPressedEvent, new PointerEventHandler(OnGlobalPointerPressed), true);

            // 2. Parallelize independent startup tasks to reduce total load time.
            var brushTask = SetPlatformSpecificBrushAsync();
            var navTask = PopulateNavigationAsync();
            var animTask = _settingsService.GetPlayerAnimationEnabledAsync();
            var paneTask = RestorePaneStateAsync();

            await Task.WhenAll(brushTask, navTask, animTask, paneTask);

            // 3. Post-initialization UI synchronization.
            _isPlayerAnimationEnabled = await animTask;

            if (NavView.MenuItems.Any() && NavView.SelectedItem == null)
            {
                NavView.SelectedItem = NavView.MenuItems.First();
            }

            UpdateNavViewSelection(ContentFrame.CurrentSourcePageType);

            _ = ApplyDynamicThemeForCurrentTrackAsync();
            UpdatePlayerVisualState(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during MainPage initialization");
        }
    }

    // Cleans up event handlers when the page is unloaded.
    private void OnMainPageUnloaded(object sender, RoutedEventArgs e)
    {
        _isUnloaded = true;
        ActualThemeChanged -= OnActualThemeChanged;
        ContentFrame.Navigated -= OnContentFrameNavigated;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _settingsService.PlayerAnimationSettingChanged -= OnPlayerAnimationSettingChanged;
        _settingsService.NavigationSettingsChanged -= OnNavigationSettingsChanged;
        _settingsService.TransparencyEffectsSettingChanged -= OnTransparencyEffectsSettingChanged;
        _settingsService.PlayerDesignSettingsChanged -= OnPlayerDesignSettingsChanged;

        // Dispose the tray icon control to prevent "Exception Processing Message 0xc0000005" errors on exit.
        AppTrayIconHost?.Dispose();
        
        RemoveHandler(PointerPressedEvent, (PointerEventHandler)OnGlobalPointerPressed);
    }

    /// <summary>
    ///     Sets the background brush for the floating player based on user settings and OS capabilities.
    ///     Handles switching between Acrylic (glass) and Solid (opaque) materials.
    /// </summary>
    private async Task SetPlatformSpecificBrushAsync()
    {
        var isAcrylicEnabled = _settingsService.IsTransparencyEffectsEnabled();
        var material = await _settingsService.GetPlayerBackgroundMaterialAsync();

        if (!isAcrylicEnabled || material == PlayerBackgroundMaterial.Solid)
        {
            // Use solid color (respects system transparency preference or explicit user choice)
            // We use PlayerTintColorBrush directly to ensure the background opacity is 1.0 (Solid)
            // but the color matches the calculated tint.
            FloatingPlayerContainer.Background = (Brush)Application.Current.Resources["PlayerTintColorBrush"];
        }
        else if (_win32InteropService.IsWindows11OrNewer)
        {
            // We are on Windows 11 or newer - use Acrylic
            FloatingPlayerContainer.Background = (Brush)Application.Current.Resources["Win11AcrylicBrush"];
        }
        else
        {
            // We are on Windows 10 - use Win10 Acrylic
            FloatingPlayerContainer.Background = (Brush)Application.Current.Resources["Win10AcrylicBrush"];
        }
    }

    private void OnPlayerDesignSettingsChanged()
    {
        _dispatcherService.TryEnqueue(async () =>
        {
            if (_isUnloaded) return;
            
            // 1. Update the brush (Material change)
            await SetPlatformSpecificBrushAsync();
            
            // 2. Re-calculate the tint color (Intensity change)
            await _themeService.ReapplyCurrentDynamicThemeAsync();
        });
    }

    private void OnTransparencyEffectsSettingChanged(bool isEnabled)
    {
        _dispatcherService.TryEnqueue(async () =>
        {
            if (_isUnloaded) return;
            await SetPlatformSpecificBrushAsync();
        });
    }

    // Responds to changes in the player animation setting.
    private void OnPlayerAnimationSettingChanged(bool isEnabled)
    {
        _dispatcherService.TryEnqueue(() =>
        {
            if (_isUnloaded) return;
            _isPlayerAnimationEnabled = isEnabled;
            UpdatePlayerVisualState(false);
        });
    }

    // Repopulates the navigation view when its settings change.
    private void OnNavigationSettingsChanged()
    {
        // Use EnqueueAsync for async lambdas. Fire-and-forget is acceptable
        // for this background UI update. The discard `_ =` signifies this intent.
        _ = _dispatcherService.EnqueueAsync(async () =>
        {
            if (_isUnloaded) return;
            
            _isUpdatingNavViewSelection = true;
            await PopulateNavigationAsync();
            if (ContentFrame.CurrentSourcePageType == typeof(SettingsPage)) NavView.SelectedItem = NavView.SettingsItem;
            _isUpdatingNavViewSelection = false;
        });
    }

    // Reapplies the dynamic theme when the system or app theme changes.
    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        if (ActualTheme == _lastKnownTheme) return;
        _lastKnownTheme = ActualTheme;
        _ = _themeService.ReapplyCurrentDynamicThemeAsync();
    }

    // Responds to property changes in the PlayerViewModel.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _dispatcherService.TryEnqueue(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(PlayerViewModel.CurrentPlayingTrack):
                    _ = ApplyDynamicThemeForCurrentTrackAsync();
                    UpdatePlayerVisualState();
                    break;
                case nameof(PlayerViewModel.IsPlaying):
                    _ = SetPlatformSpecificBrushAsync();
                    UpdatePlayerVisualState(); // Expand or collapse the player based on playback state
                    break;
            }
        });
    }

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        HandleNavigation(args.IsSettingsInvoked, args.InvokedItemContainer, args.RecommendedNavigationTransitionInfo);
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_isUpdatingNavViewSelection) return;
        HandleNavigation(args.IsSettingsSelected, args.SelectedItem, args.RecommendedNavigationTransitionInfo);
    }

    private void NavView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        TryGoBack();
    }

    // Updates UI elements like the back button after a navigation event.
    private void OnContentFrameNavigated(object sender, NavigationEventArgs e)
    {
        try
        {
            var isDetailPage = _detailPageToParentTagMap.ContainsKey(e.SourcePageType);
            var isLyricsPage = e.SourcePageType == typeof(LyricsPage);
            
            if (AppTitleBar != null)
            {
                AppTitleBar.IsBackButtonVisible = (ContentFrame.CanGoBack && isDetailPage) || isLyricsPage;
            }

            UpdateNavViewSelection(e.SourcePageType);

            var isSettingsPage = e.SourcePageType == typeof(SettingsPage);
            var stateName = isSettingsPage ? "PlayerHidden" : "PlayerVisible";
            VisualStateManager.GoToState(this, stateName, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing OnContentFrameNavigated");
        }
    }

    private void FloatingPlayerContainer_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverPlayer = true;
        UpdatePlayerVisualState();
    }

    private void FloatingPlayerContainer_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverPlayer = false;
        UpdatePlayerVisualState();
    }

    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
        // Save the pane state if the setting is enabled.
        _ = SavePaneStateAsync(NavView.IsPaneOpen);
    }

    private void AppTitleBar_BackRequested(TitleBar sender, object args)
    {
        TryGoBack();
    }

    private void QueueFlyout_Opened(object sender, object e)
    {
        _isQueueFlyoutOpen = true;
        UpdatePlayerVisualState();
    }

    private void QueueFlyout_Closed(object sender, object e)
    {
        _isQueueFlyoutOpen = false;
        UpdatePlayerVisualState();
    }

    private void MediaSeekerSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        ViewModel.IsUserDraggingSlider = true;
    }

    private void MediaSeekerSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        ViewModel.IsUserDraggingSlider = false;
    }

    private void MediaSeekerSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        ViewModel.IsUserDraggingSlider = false;
    }

    private void MediaSeekerSlider_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Home or VirtualKey.End)
        {
            ViewModel.IsUserDraggingSlider = true;
        }
    }

    private void MediaSeekerSlider_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Home or VirtualKey.End)
        {
            ViewModel.IsUserDraggingSlider = false;
        }
    }

    /// <summary>
    ///     Saves the navigation pane state if the "remember pane state" setting is enabled.
    /// </summary>
    private async Task SavePaneStateAsync(bool isOpen)
    {
        try
        {
            if (await _settingsService.GetRememberPaneStateEnabledAsync())
                await _settingsService.SetLastPaneOpenAsync(isOpen);
        }
        catch (Exception ex)
        {
            // Non-critical: log at trace level and continue.
            _logger.LogTrace(ex, "Failed to save navigation pane state.");
        }
    }

    private void VolumeSliderContainer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var pointerPoint = e.GetCurrentPoint((UIElement)sender);
        var delta = pointerPoint.Properties.MouseWheelDelta;

        if (delta > 0)
            ViewModel.CurrentVolume = Math.Min(100, ViewModel.CurrentVolume + VolumeChangeStep);
        else if (delta < 0)
            ViewModel.CurrentVolume = Math.Max(0, ViewModel.CurrentVolume - VolumeChangeStep);

        e.Handled = true;
    }

    /// <summary>
    ///     Restores the navigation pane state if the "remember pane state" setting is enabled.
    ///     This overrides the XAML adaptive trigger's default state.
    /// </summary>
    private async Task RestorePaneStateAsync()
    {
        try
        {
            if (await _settingsService.GetRememberPaneStateEnabledAsync())
            {
                var savedState = await _settingsService.GetLastPaneOpenAsync();
                if (savedState.HasValue) NavView.IsPaneOpen = savedState.Value;
            }
        }
        catch (Exception ex)
        {
            // Non-critical: log at trace level and let adaptive triggers handle default state.
            _logger.LogTrace(ex, "Failed to restore navigation pane state.");
        }
    }

    /// <summary>
    ///     Handles global pointer pressed events to enable back navigation using the mouse side button.
    ///     This is registered with handledEventsToo: true to catch events even if child controls swallow them.
    /// </summary>
    private void OnGlobalPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(sender as UIElement);
        var properties = point.Properties;

        if (properties.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.XButton1Pressed)
        {
            TryGoBack();
            e.Handled = true;
        }
    }

}

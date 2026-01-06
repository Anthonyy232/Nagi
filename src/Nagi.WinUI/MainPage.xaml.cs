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
using Nagi.WinUI.Services.Abstractions;
using Nagi.WinUI.ViewModels;
using Nagi.WinUI.Helpers;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;

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
        { typeof(PlaylistSongViewPage), "playlists" },
        { typeof(SmartPlaylistSongViewPage), "playlists" },
        { typeof(FolderSongViewPage), "folders" },
        { typeof(ArtistViewPage), "artists" },
        { typeof(AlbumViewPage), "albums" },
        { typeof(GenreViewPage), "genres" }
    };

    private readonly IDispatcherService _dispatcherService;
    private readonly ILogger<MainPage> _logger;

    // Maps navigation item tags to their corresponding page types.
    private readonly Dictionary<string, Type> _pages = new()
    {
        { "library", typeof(LibraryPage) },
        { "folders", typeof(FolderPage) },
        { "playlists", typeof(PlaylistPage) },
        { "settings", typeof(SettingsPage) },
        { "artists", typeof(ArtistPage) },
        { "albums", typeof(AlbumPage) },
        { "genres", typeof(GenrePage) }
    };

    private readonly IUISettingsService _settingsService;
    private readonly IThemeService _themeService;
    private readonly ILocalizationService _localizationService;

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

    public MainPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<PlayerViewModel>();
        _settingsService = App.Services!.GetRequiredService<IUISettingsService>();
        _themeService = App.Services!.GetRequiredService<IThemeService>();
        _dispatcherService = App.Services!.GetRequiredService<IDispatcherService>();
        _localizationService = App.Services!.GetRequiredService<ILocalizationService>();
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
        if (ContentFrame.CanGoBack) ContentFrame.GoBack();
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
    private void ApplyDynamicThemeForCurrentTrack()
    {
        var song = ViewModel.CurrentPlayingTrack;
        _themeService.ApplyDynamicThemeFromSwatches(song?.LightSwatchId, song?.DarkSwatchId);
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
            var navViewItem = new NavigationViewItem
            {
                Content = _localizationService.GetString($"NavItem_{item.Tag}", item.DisplayName),
                Tag = item.Tag,
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
        SetPlatformSpecificBrush();

        await GenerateAndSetTaskbarIconsAsync();

        ActualThemeChanged += OnActualThemeChanged;
        ContentFrame.Navigated += OnContentFrameNavigated;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        _settingsService.PlayerAnimationSettingChanged += OnPlayerAnimationSettingChanged;
        _settingsService.NavigationSettingsChanged += OnNavigationSettingsChanged;
        _settingsService.TransparencyEffectsSettingChanged += OnTransparencyEffectsSettingChanged;

        await PopulateNavigationAsync();

        if (NavView.MenuItems.Any() && NavView.SelectedItem == null) NavView.SelectedItem = NavView.MenuItems.First();
        UpdateNavViewSelection(ContentFrame.CurrentSourcePageType);

        _isPlayerAnimationEnabled = await _settingsService.GetPlayerAnimationEnabledAsync();

        // Restore navigation pane state if the setting is enabled.
        await RestorePaneStateAsync();

        ApplyDynamicThemeForCurrentTrack();
        UpdatePlayerVisualState(false);
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

        // Dispose the tray icon control to prevent "Exception Processing Message 0xc0000005" errors on exit.
        AppTrayIconHost?.Dispose();
    }

    /// <summary>
    ///     Sets the background brush for the floating player based on the current OS.
    ///     Uses Acrylic for Windows 11+ and a solid color for Windows 10.
    /// </summary>
    private void SetPlatformSpecificBrush()
    {
        var isAcrylicEnabled = _settingsService.IsTransparencyEffectsEnabled();
        if (!isAcrylicEnabled)
            // Transparency effects are disabled
            FloatingPlayerContainer.Background = (Brush)Application.Current.Resources["NonTransparentBrush"];
        else if (ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 14))
            // We are on Windows 11 or newer
            FloatingPlayerContainer.Background = (Brush)Application.Current.Resources["Win11AcrylicBrush"];
        else
            // We are on Windows 10
            FloatingPlayerContainer.Background = (Brush)Application.Current.Resources["Win10AcrylicBrush"];
    }

    private void OnTransparencyEffectsSettingChanged(bool isEnabled)
    {
        _dispatcherService.TryEnqueue(() =>
        {
            if (_isUnloaded) return;
            SetPlatformSpecificBrush();
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
        _themeService.ReapplyCurrentDynamicTheme();
        _ = GenerateAndSetTaskbarIconsAsync();
    }

    private async Task GenerateAndSetTaskbarIconsAsync()
    {
        try
        {
            var taskbarService = App.Services?.GetRequiredService<ITaskbarService>();
            if (taskbarService == null) return;

            // We must wait for the UI to be ready.
            // If the controls are not laid out, render target bitmap might fail or return empty.
            if (PreviousIcon.ActualWidth == 0)
            {
                 // Wait a short bit for layout to settle.
                 // Since items have fixed size and are in tree, they should arrange quickly.
                 await Task.Delay(50);
            }

            var prevIcon = await RenderIconToHIcon(PreviousIcon);
            var playIcon = await RenderIconToHIcon(PlayIcon);
            var pauseIcon = await RenderIconToHIcon(PauseIcon);
            var nextIcon = await RenderIconToHIcon(NextIcon);

            taskbarService.UpdateIcons(prevIcon, playIcon, pauseIcon, nextIcon);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to generate taskbar icons");
        }
    }

    private async Task<nint> RenderIconToHIcon(UIElement element)
    {
        // 1. Render the XAML element to a bitmap
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(element);
        var pixelBuffer = await rtb.GetPixelsAsync();

        var width = rtb.PixelWidth;
        var height = rtb.PixelHeight;

        // 2. Get raw pixels (BGRA8 format)
        using var stream = pixelBuffer.AsStream();
        var pixels = new byte[stream.Length];
        stream.Read(pixels, 0, pixels.Length);

        // 3. Create a DIB Section (Device Independent Bitmap) to preserve Alpha channel.
        // GDI's standard CreateBitmap often ignores alpha, resulting in black backgrounds.
        // A DIB section is required for proper alpha transparency with CreateIconIndirect.

        var bmi = new TaskbarNativeMethods.BITMAPINFO
        {
            bmiHeader = new TaskbarNativeMethods.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<TaskbarNativeMethods.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // Negative height for top-down DIB
                biPlanes = 1,
                biBitCount = 32,
                biCompression = TaskbarNativeMethods.BI_RGB
            }
        };

        var hDC = IntPtr.Zero; // Use screen DC or NULL
        var hBmColor = TaskbarNativeMethods.CreateDIBSection(hDC, ref bmi, TaskbarNativeMethods.DIB_RGB_COLORS, out var ppvBits, IntPtr.Zero, 0);

        if (hBmColor == IntPtr.Zero)
        {
            _logger.LogError("CreateDIBSection failed.");
            return IntPtr.Zero;
        }

        // Copy our BGRA pixels into the DIB section's memory
        Marshal.Copy(pixels, 0, ppvBits, pixels.Length);

        // 4. Create an empty mask bitmap (1bpp).
        // For alpha icons, the mask is ignored but must be present.
        var hBmMask = TaskbarNativeMethods.CreateBitmap(width, height, 1, 1, null);

        // 5. Create the HICON
        var iconInfo = new TaskbarNativeMethods.ICONINFO
        {
            fIcon = true,
            xHotspot = 0,
            yHotspot = 0,
            hbmMask = hBmMask,
            hbmColor = hBmColor
        };

        var hIcon = TaskbarNativeMethods.CreateIconIndirect(ref iconInfo);

        // 6. Cleanup GDI objects (the icon has its own copy now)
        TaskbarNativeMethods.DeleteObject(hBmColor);
        TaskbarNativeMethods.DeleteObject(hBmMask);

        return hIcon;
    }

    // Responds to property changes in the PlayerViewModel.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.CurrentPlayingTrack):
                ApplyDynamicThemeForCurrentTrack();
                UpdatePlayerVisualState();
                break;
            case nameof(PlayerViewModel.IsPlaying):
                SetPlatformSpecificBrush();
                UpdatePlayerVisualState(); // Expand or collapse the player based on playback state
                break;
        }
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
        var isDetailPage = _detailPageToParentTagMap.ContainsKey(e.SourcePageType);
        var isLyricsPage = e.SourcePageType == typeof(LyricsPage);
        AppTitleBar.IsBackButtonVisible = (ContentFrame.CanGoBack && isDetailPage) || isLyricsPage;

        UpdateNavViewSelection(e.SourcePageType);

        var isSettingsPage = e.SourcePageType == typeof(SettingsPage);
        var stateName = isSettingsPage ? "PlayerHidden" : "PlayerVisible";
        VisualStateManager.GoToState(this, stateName, true);
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

}

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation.Metadata;
using Microsoft.Extensions.DependencyInjection;
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
        { typeof(FolderSongViewPage), "folders" },
        { typeof(ArtistViewPage), "artists" },
        { typeof(AlbumViewPage), "albums" },
        { typeof(GenreViewPage), "genres" }
    };

    private readonly IDispatcherService _dispatcherService;

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

    // A flag to control the player's expand/collapse animation based on user settings.
    private bool _isPlayerAnimationEnabled = true;

    // A flag to track if the pointer is currently over the floating player control.
    private bool _isPointerOverPlayer;

    // A flag to track if the queue flyout is open, to keep the player expanded.
    private bool _isQueueFlyoutOpen;

    // A flag to prevent re-entrant navigation while the selection is being updated programmatically.
    private bool _isUpdatingNavViewSelection;

    public MainPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<PlayerViewModel>();
        _settingsService = App.Services!.GetRequiredService<IUISettingsService>();
        _themeService = App.Services!.GetRequiredService<IThemeService>();
        _dispatcherService = App.Services!.GetRequiredService<IDispatcherService>(); // Injected the service
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
            ContentFrame.CurrentSourcePageType != pageType)
            ContentFrame.Navigate(pageType, null, transitionInfo);
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

        VisualStateManager.GoToState(this, stateName, useTransitions);
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
                Content = item.DisplayName,
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

        ApplyDynamicThemeForCurrentTrack();
        UpdatePlayerVisualState(false);
    }

    // Cleans up event handlers when the page is unloaded.
    private void OnMainPageUnloaded(object sender, RoutedEventArgs e)
    {
        ActualThemeChanged -= OnActualThemeChanged;
        ContentFrame.Navigated -= OnContentFrameNavigated;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _settingsService.PlayerAnimationSettingChanged -= OnPlayerAnimationSettingChanged;
        _settingsService.NavigationSettingsChanged -= OnNavigationSettingsChanged;
        _settingsService.TransparencyEffectsSettingChanged -= OnTransparencyEffectsSettingChanged;
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
        _dispatcherService.TryEnqueue(SetPlatformSpecificBrush);
    }

    // Responds to changes in the player animation setting.
    private void OnPlayerAnimationSettingChanged(bool isEnabled)
    {
        _dispatcherService.TryEnqueue(() =>
        {
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
    }

    // Responds to property changes in the PlayerViewModel.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.CurrentPlayingTrack):
                _dispatcherService.TryEnqueue(() =>
                {
                    ApplyDynamicThemeForCurrentTrack();
                    UpdatePlayerVisualState();
                });
                break;
            case nameof(PlayerViewModel.IsPlaying):
                _dispatcherService.TryEnqueue(() => SetPlatformSpecificBrush());
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

    // Allows changing the volume by scrolling the mouse wheel over the volume controls.
    private void VolumeControlsWrapper_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(sender as UIElement).Properties.MouseWheelDelta;
        var change = delta > 0 ? VolumeChangeStep : -VolumeChangeStep;
        ViewModel.CurrentVolume = Math.Clamp(ViewModel.CurrentVolume + change, 0, 100);
        e.Handled = true;
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
}
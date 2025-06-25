using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Interfaces;
using Nagi.Pages;
using Nagi.Services;
using Nagi.Services.Abstractions;
using Nagi.ViewModels;

namespace Nagi;

/// <summary>
///     Represents the main shell of the application, hosting the primary navigation,
///     content frame, and media player controls. It also provides the custom title bar.
/// </summary>
public sealed partial class MainPage : UserControl, ICustomTitleBarProvider
{
    private const double VolumeChangeStep = 5.0;

    // Maps detail page types to their parent navigation tag to keep the correct
    // navigation item selected when viewing a detail page.
    private readonly Dictionary<Type, string> _detailPageToParentTagMap = new()
    {
        { typeof(PlaylistSongViewPage), "playlists" },
        { typeof(FolderSongViewPage), "folders" },
        { typeof(ArtistViewPage), "artists" },
        { typeof(AlbumViewPage), "albums" }
    };

    // Maps navigation view item tags to their corresponding page types for navigation.
    private readonly Dictionary<string, Type> _pages = new()
    {
        { "library", typeof(LibraryPage) },
        { "folders", typeof(FolderPage) },
        { "playlists", typeof(PlaylistPage) },
        { "settings", typeof(SettingsPage) },
        { "artists", typeof(ArtistPage) },
        { "albums", typeof(AlbumPage) }
    };

    private readonly ISettingsService _settingsService;
    private bool _isPlayerAnimationEnabled = true;
    private bool _isPointerOverPlayer;

    public MainPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<PlayerViewModel>();
        _settingsService = App.Services.GetRequiredService<ISettingsService>();
        DataContext = ViewModel;

        InitializeNavigationService();

        Loaded += OnMainPageLoaded;
        Unloaded += OnMainPageUnloaded;
        ActualThemeChanged += OnActualThemeChanged;
        ContentFrame.Navigated += OnContentFrameNavigated;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    public PlayerViewModel ViewModel { get; }

    public Grid GetAppTitleBarElement()
    {
        return AppTitleBar;
    }

    private void InitializeNavigationService()
    {
        var navigationService = App.Services.GetRequiredService<INavigationService>();
        navigationService.Initialize(ContentFrame);
    }

    /// <summary>
    ///     Updates the visual state of the title bar based on window activation.
    /// </summary>
    /// <param name="activationState">The current activation state of the window.</param>
    public void UpdateActivationVisualState(WindowActivationState activationState)
    {
        var stateName = activationState == WindowActivationState.Deactivated ? "WindowIsInactive" : "WindowIsActive";
        VisualStateManager.GoToState(this, stateName, true);
    }

    private void HandleNavigation(bool isSettings, object? selectedItem, NavigationTransitionInfo transitionInfo)
    {
        if (isSettings)
        {
            if (ContentFrame.CurrentSourcePageType != typeof(SettingsPage))
                ContentFrame.Navigate(typeof(SettingsPage), null, transitionInfo);
            return;
        }

        if (selectedItem is NavigationViewItem navItem && navItem.Tag is string tag)
            if (_pages.TryGetValue(tag, out var pageType) && ContentFrame.CurrentSourcePageType != pageType)
                ContentFrame.Navigate(pageType, null, transitionInfo);
    }

    private void TryGoBack()
    {
        if (ContentFrame.CanGoBack) ContentFrame.GoBack();
    }

    /// <summary>
    ///     Ensures the correct NavigationView item is selected after a navigation operation.
    ///     This is especially important for keeping parent categories selected when on a detail page.
    /// </summary>
    private void UpdateNavViewSelection(Type currentPageType)
    {
        if (currentPageType == typeof(SettingsPage))
        {
            NavView.SelectedItem = NavView.SettingsItem;
            return;
        }

        var tagToSelect = _pages.FirstOrDefault(p => p.Value == currentPageType).Key
                          ?? (_detailPageToParentTagMap.TryGetValue(currentPageType, out var parentTag)
                              ? parentTag
                              : null);

        if (tagToSelect != null)
            NavView.SelectedItem = NavView.MenuItems
                .OfType<NavigationViewItem>()
                .FirstOrDefault(menuItem => menuItem.Tag?.ToString() == tagToSelect);
    }

    /// <summary>
    ///     Applies a dynamic theme based on the album art of the currently playing track.
    /// </summary>
    private void ApplyDynamicThemeForCurrentTrack()
    {
        var song = ViewModel.CurrentPlayingTrack;
        if (App.Current is App app) app.ApplyDynamicThemeFromSwatches(song?.LightSwatchId, song?.DarkSwatchId);
    }

    /// <summary>
    ///     Manages the visibility and state of the floating player controls.
    ///     The player is expanded if music is playing, the user is hovering over it, or animations are disabled.
    /// </summary>
    private void UpdatePlayerVisualState(bool useTransitions = true)
    {
        var isPlaying = ViewModel.CurrentPlayingTrack != null && ViewModel.IsPlaying;
        var shouldBeExpanded = !_isPlayerAnimationEnabled || isPlaying || _isPointerOverPlayer;
        var stateName = shouldBeExpanded ? "PlayerExpanded" : "PlayerCollapsed";

        VisualStateManager.GoToState(this, stateName, useTransitions);
    }

    private async void OnMainPageLoaded(object sender, RoutedEventArgs e)
    {
        if (NavView.MenuItems.Any() && NavView.SelectedItem == null)
            NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault()
                                   ?? NavView.MenuItems.First();

        _isPlayerAnimationEnabled = await _settingsService.GetPlayerAnimationEnabledAsync();
        _settingsService.PlayerAnimationSettingChanged += OnPlayerAnimationSettingChanged;

        ApplyDynamicThemeForCurrentTrack();
        UpdatePlayerVisualState(false);
    }

    private void OnMainPageUnloaded(object sender, RoutedEventArgs e)
    {
        _settingsService.PlayerAnimationSettingChanged -= OnPlayerAnimationSettingChanged;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnPlayerAnimationSettingChanged(bool isEnabled)
    {
        // Must run on the UI thread as the event may be raised from a background thread.
        DispatcherQueue.TryEnqueue(() =>
        {
            _isPlayerAnimationEnabled = isEnabled;
            UpdatePlayerVisualState(false);
        });
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        ApplyDynamicThemeForCurrentTrack();
        if (App.RootWindow is MainWindow mainWindow) mainWindow.UpdateCaptionButtonColors();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.CurrentPlayingTrack))
            // Ensure theme updates happen on the UI thread.
            DispatcherQueue.TryEnqueue(ApplyDynamicThemeForCurrentTrack);

        if (e.PropertyName is nameof(PlayerViewModel.CurrentPlayingTrack) or nameof(PlayerViewModel.IsPlaying))
            // Ensure visual state updates happen on the UI thread.
            DispatcherQueue.TryEnqueue(() => UpdatePlayerVisualState());
    }

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        HandleNavigation(args.IsSettingsInvoked, args.InvokedItemContainer, args.RecommendedNavigationTransitionInfo);
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        HandleNavigation(args.IsSettingsSelected, args.SelectedItem, args.RecommendedNavigationTransitionInfo);
    }

    private void NavView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        TryGoBack();
    }

    private void CustomBackButton_Click(object sender, RoutedEventArgs e)
    {
        TryGoBack();
    }

    private void OnContentFrameNavigated(object sender, NavigationEventArgs e)
    {
        var isDetailPage = _detailPageToParentTagMap.ContainsKey(e.SourcePageType);
        CustomBackButton.Visibility =
            ContentFrame.CanGoBack && isDetailPage ? Visibility.Visible : Visibility.Collapsed;
        UpdateNavViewSelection(e.SourcePageType);
    }

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
}
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
using Nagi.Services.Abstractions;
using Nagi.ViewModels;

namespace Nagi;

/// <summary>
/// The main shell of the application, hosting navigation, content, and the media player.
/// This page also provides the custom title bar elements to the main window.
/// </summary>
public sealed partial class MainPage : UserControl, ICustomTitleBarProvider {
    private const double VolumeChangeStep = 5.0;

    // Maps detail page types to their parent navigation tag for correct item selection.
    private readonly Dictionary<Type, string> _detailPageToParentTagMap = new()
    {
        { typeof(PlaylistSongViewPage), "playlists" },
        { typeof(FolderSongViewPage), "folders" },
        { typeof(ArtistViewPage), "artists" },
        { typeof(AlbumViewPage), "albums" }
    };

    // Maps navigation view item tags to their corresponding page types.
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

    /// <summary>
    /// Initializes a new instance of the <see cref="MainPage"/> class.
    /// </summary>
    public MainPage() {
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

    /// <summary>
    /// Gets the view model for the media player.
    /// </summary>
    public PlayerViewModel ViewModel { get; }

    /// <summary>
    /// Provides the title bar UI element to the main window.
    /// </summary>
    public Grid GetAppTitleBarElement() => AppTitleBar;

    /// <summary>
    /// Provides the title bar row definition to the main window.
    /// </summary>
    public RowDefinition GetAppTitleBarRowElement() => AppTitleBarRow;

    private void InitializeNavigationService() {
        var navigationService = App.Services.GetRequiredService<INavigationService>();
        navigationService.Initialize(ContentFrame);
    }

    /// <summary>
    /// Updates the visual state of the page based on window activation.
    /// </summary>
    /// <param name="activationState">The window's activation state.</param>
    public void UpdateActivationVisualState(WindowActivationState activationState) {
        string stateName = activationState == WindowActivationState.Deactivated
            ? "WindowIsInactive"
            : "WindowIsActive";
        VisualStateManager.GoToState(this, stateName, true);
    }

    private void HandleNavigation(bool isSettings, object? selectedItem, NavigationTransitionInfo transitionInfo) {
        if (isSettings) {
            if (ContentFrame.CurrentSourcePageType != typeof(SettingsPage)) {
                ContentFrame.Navigate(typeof(SettingsPage), null, transitionInfo);
            }
            return;
        }

        if (selectedItem is NavigationViewItem { Tag: string tag } &&
            _pages.TryGetValue(tag, out Type? pageType) &&
            ContentFrame.CurrentSourcePageType != pageType) {
            ContentFrame.Navigate(pageType, null, transitionInfo);
        }
    }

    private void TryGoBack() {
        if (ContentFrame.CanGoBack) {
            ContentFrame.GoBack();
        }
    }

    private void UpdateNavViewSelection(Type currentPageType) {
        if (currentPageType == typeof(SettingsPage)) {
            NavView.SelectedItem = NavView.SettingsItem;
            return;
        }

        // Find the navigation tag associated with the current page.
        // This could be a direct match or a parent category for a detail page.
        string? tagToSelect = _pages.FirstOrDefault(p => p.Value == currentPageType).Key;
        if (tagToSelect is null) {
            _detailPageToParentTagMap.TryGetValue(currentPageType, out tagToSelect);
        }

        if (tagToSelect != null) {
            NavView.SelectedItem = NavView.MenuItems
                .OfType<NavigationViewItem>()
                .FirstOrDefault(menuItem => menuItem.Tag?.ToString() == tagToSelect);
        }
    }

    private void ApplyDynamicThemeForCurrentTrack() {
        var song = ViewModel.CurrentPlayingTrack;
        if (App.Current is App app) {
            app.ApplyDynamicThemeFromSwatches(song?.LightSwatchId, song?.DarkSwatchId);
        }
    }

    private void UpdatePlayerVisualState(bool useTransitions = true) {
        bool isPlaying = ViewModel.CurrentPlayingTrack != null && ViewModel.IsPlaying;

        // The player should be expanded if music is playing, the user is hovering over it,
        // or if fancy animations are disabled in settings.
        bool shouldBeExpanded = !_isPlayerAnimationEnabled || isPlaying || _isPointerOverPlayer;
        string stateName = shouldBeExpanded ? "PlayerExpanded" : "PlayerCollapsed";

        VisualStateManager.GoToState(this, stateName, useTransitions);
    }

    private async void OnMainPageLoaded(object sender, RoutedEventArgs e) {
        // Select the first item if nothing is selected.
        if (NavView.MenuItems.Any() && NavView.SelectedItem == null) {
            NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault()
                                   ?? NavView.MenuItems.First();
        }

        _isPlayerAnimationEnabled = await _settingsService.GetPlayerAnimationEnabledAsync();
        _settingsService.PlayerAnimationSettingChanged += OnPlayerAnimationSettingChanged;

        ApplyDynamicThemeForCurrentTrack();
        UpdatePlayerVisualState(false);
    }

    private void OnMainPageUnloaded(object sender, RoutedEventArgs e) {
        _settingsService.PlayerAnimationSettingChanged -= OnPlayerAnimationSettingChanged;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnPlayerAnimationSettingChanged(bool isEnabled) {
        // Ensure UI updates happen on the main thread.
        DispatcherQueue.TryEnqueue(() => {
            _isPlayerAnimationEnabled = isEnabled;
            UpdatePlayerVisualState(false);
        });
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args) {
        ApplyDynamicThemeForCurrentTrack();
        if (App.RootWindow is MainWindow mainWindow) {
            mainWindow.UpdateCaptionButtonColors();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName == nameof(PlayerViewModel.CurrentPlayingTrack)) {
            DispatcherQueue.TryEnqueue(ApplyDynamicThemeForCurrentTrack);
        }

        if (e.PropertyName is nameof(PlayerViewModel.CurrentPlayingTrack) or nameof(PlayerViewModel.IsPlaying)) {
            DispatcherQueue.TryEnqueue(() => UpdatePlayerVisualState());
        }
    }

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args) {
        HandleNavigation(args.IsSettingsInvoked, args.InvokedItemContainer, args.RecommendedNavigationTransitionInfo);
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args) {
        HandleNavigation(args.IsSettingsSelected, args.SelectedItem, args.RecommendedNavigationTransitionInfo);
    }

    private void NavView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args) => TryGoBack();

    private void CustomBackButton_Click(object sender, RoutedEventArgs e) => TryGoBack();

    private void OnContentFrameNavigated(object sender, NavigationEventArgs e) {
        // Show a custom back button only for specific detail pages.
        bool isDetailPage = _detailPageToParentTagMap.ContainsKey(e.SourcePageType);
        CustomBackButton.Visibility = ContentFrame.CanGoBack && isDetailPage
            ? Visibility.Visible
            : Visibility.Collapsed;

        UpdateNavViewSelection(e.SourcePageType);
    }

    private void VolumeControlsWrapper_PointerWheelChanged(object sender, PointerRoutedEventArgs e) {
        var delta = e.GetCurrentPoint(sender as UIElement).Properties.MouseWheelDelta;
        double change = delta > 0 ? VolumeChangeStep : -VolumeChangeStep;
        ViewModel.CurrentVolume = Math.Clamp(ViewModel.CurrentVolume + change, 0, 100);
        e.Handled = true;
    }

    private void FloatingPlayerContainer_PointerEntered(object sender, PointerRoutedEventArgs e) {
        _isPointerOverPlayer = true;
        UpdatePlayerVisualState();
    }

    private void FloatingPlayerContainer_PointerExited(object sender, PointerRoutedEventArgs e) {
        _isPointerOverPlayer = false;
        UpdatePlayerVisualState();
    }
}
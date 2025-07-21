using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Controls;
using Nagi.Interfaces;
using Nagi.Pages;
using Nagi.Services.Abstractions;
using Nagi.ViewModels;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace Nagi;

/// <summary>
/// The main shell of the application, hosting navigation, content, and the media player.
/// This page also provides the custom title bar elements to the main window.
/// </summary>
public sealed partial class MainPage : UserControl, ICustomTitleBarProvider {
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

    private readonly ISettingsService _settingsService;
    private readonly IThemeService _themeService;
    private bool _isPlayerAnimationEnabled = true;
    private bool _isPointerOverPlayer;
    private bool _isQueueFlyoutOpen;

    // Flag to prevent re-entrant or recursive navigation calls while the selection is being updated programmatically.
    private bool _isUpdatingNavViewSelection;

    public MainPage() {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<PlayerViewModel>();
        _settingsService = App.Services!.GetRequiredService<ISettingsService>();
        _themeService = App.Services!.GetRequiredService<IThemeService>();
        DataContext = ViewModel;

        InitializeNavigationService();

        Loaded += OnMainPageLoaded;
        Unloaded += OnMainPageUnloaded;
    }

    public PlayerViewModel ViewModel { get; }
    public TitleBar GetAppTitleBarElement() => AppTitleBar;
    public RowDefinition GetAppTitleBarRowElement() => AppTitleBarRow;

    private void InitializeNavigationService() {
        var navigationService = App.Services!.GetRequiredService<INavigationService>();
        navigationService.Initialize(ContentFrame);
    }

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
        _isUpdatingNavViewSelection = true;

        if (currentPageType == typeof(SettingsPage)) {
            NavView.SelectedItem = NavView.SettingsItem;
        }
        else {
            string? tagToSelect = _pages.FirstOrDefault(p => p.Value == currentPageType).Key;
            if (tagToSelect is null) {
                _detailPageToParentTagMap.TryGetValue(currentPageType, out tagToSelect);
            }

            if (tagToSelect != null) {
                NavView.SelectedItem = NavView.MenuItems
                    .OfType<NavigationViewItem>()
                    .FirstOrDefault(menuItem => menuItem.Tag?.ToString() == tagToSelect);
            }
            else {
                NavView.SelectedItem = null;
            }
        }

        _isUpdatingNavViewSelection = false;
    }

    private void ApplyDynamicThemeForCurrentTrack() {
        var song = ViewModel.CurrentPlayingTrack;
        _themeService.ApplyDynamicThemeFromSwatches(song?.LightSwatchId, song?.DarkSwatchId);
    }

    private void UpdatePlayerVisualState(bool useTransitions = true) {
        bool isPlaying = ViewModel.CurrentPlayingTrack != null && ViewModel.IsPlaying;

        // The player is expanded if animations are disabled, music is playing, the user is hovering, or the queue flyout is open.
        bool shouldBeExpanded = !_isPlayerAnimationEnabled || isPlaying || _isPointerOverPlayer || _isQueueFlyoutOpen;
        string stateName = shouldBeExpanded ? "PlayerExpanded" : "PlayerCollapsed";

        VisualStateManager.GoToState(this, stateName, useTransitions);
    }

    private async Task PopulateNavigationAsync() {
        NavView.MenuItems.Clear();
        var navItems = await _settingsService.GetNavigationItemsAsync();

        foreach (var item in navItems.Where(i => i.IsEnabled)) {
            var navViewItem = new NavigationViewItem {
                Content = item.DisplayName,
                Tag = item.Tag,
                Icon = new FontIcon { Glyph = item.IconGlyph }
            };

            if (!string.IsNullOrEmpty(item.IconFontFamily)) {
                if (navViewItem.Icon is FontIcon icon) {
                    icon.FontFamily = new FontFamily(item.IconFontFamily);
                }
            }

            NavView.MenuItems.Add(navViewItem);
        }
    }

    private async void OnMainPageLoaded(object sender, RoutedEventArgs e) {
        ActualThemeChanged += OnActualThemeChanged;
        ContentFrame.Navigated += OnContentFrameNavigated;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        _settingsService.PlayerAnimationSettingChanged += OnPlayerAnimationSettingChanged;
        _settingsService.NavigationSettingsChanged += OnNavigationSettingsChanged;

        await PopulateNavigationAsync();

        if (NavView.MenuItems.Any() && NavView.SelectedItem == null) {
            NavView.SelectedItem = NavView.MenuItems.First();
        }
        UpdateNavViewSelection(ContentFrame.CurrentSourcePageType);

        _isPlayerAnimationEnabled = await _settingsService.GetPlayerAnimationEnabledAsync();

        ApplyDynamicThemeForCurrentTrack();
        UpdatePlayerVisualState(false);
    }

    private void OnMainPageUnloaded(object sender, RoutedEventArgs e) {
        ActualThemeChanged -= OnActualThemeChanged;
        ContentFrame.Navigated -= OnContentFrameNavigated;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _settingsService.PlayerAnimationSettingChanged -= OnPlayerAnimationSettingChanged;
        _settingsService.NavigationSettingsChanged -= OnNavigationSettingsChanged;
    }

    private void OnPlayerAnimationSettingChanged(bool isEnabled) {
        DispatcherQueue.TryEnqueue(() => {
            _isPlayerAnimationEnabled = isEnabled;
            UpdatePlayerVisualState(false);
        });
    }

    private void OnNavigationSettingsChanged() {
        DispatcherQueue.TryEnqueue(async () => {
            _isUpdatingNavViewSelection = true;
            await PopulateNavigationAsync();
            if (ContentFrame.CurrentSourcePageType == typeof(SettingsPage)) {
                NavView.SelectedItem = NavView.SettingsItem;
            }
            _isUpdatingNavViewSelection = false;
        });
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args) {
        _themeService.ReapplyCurrentDynamicTheme();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e) {
        switch (e.PropertyName) {
            case nameof(PlayerViewModel.CurrentPlayingTrack):
                DispatcherQueue.TryEnqueue(() => {
                    ApplyDynamicThemeForCurrentTrack();
                    UpdatePlayerVisualState();
                });
                break;
            case nameof(PlayerViewModel.IsPlaying):
                DispatcherQueue.TryEnqueue(() => UpdatePlayerVisualState());
                break;
        }
    }

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args) {
        HandleNavigation(args.IsSettingsInvoked, args.InvokedItemContainer, args.RecommendedNavigationTransitionInfo);
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args) {
        if (_isUpdatingNavViewSelection) return;
        HandleNavigation(args.IsSettingsSelected, args.SelectedItem, args.RecommendedNavigationTransitionInfo);
    }

    private void NavView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args) {
        TryGoBack();
    }

    private void OnContentFrameNavigated(object sender, NavigationEventArgs e) {
        bool isDetailPage = _detailPageToParentTagMap.ContainsKey(e.SourcePageType);
        AppTitleBar.IsBackButtonVisible = ContentFrame.CanGoBack && isDetailPage;

        UpdateNavViewSelection(e.SourcePageType);

        bool isSettingsPage = e.SourcePageType == typeof(SettingsPage);
        string stateName = isSettingsPage ? "PlayerHidden" : "PlayerVisible";
        VisualStateManager.GoToState(this, stateName, true);
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

    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args) {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private void AppTitleBar_BackRequested(TitleBar sender, object args) {
        TryGoBack();
    }

    private void QueueFlyout_Opened(object sender, object e) {
        _isQueueFlyoutOpen = true;
        UpdatePlayerVisualState();
    }

    private void QueueFlyout_Closed(object sender, object e) {
        _isQueueFlyoutOpen = false;
        // The visual state must be updated when the flyout closes, in case the pointer is not over the player.
        UpdatePlayerVisualState();
    }
}
using System;
using Windows.System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Models;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.ViewModels;
using Nagi.WinUI.Controls;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Pages;
using Nagi.WinUI.Services.Abstractions;
using Nagi.WinUI.Services.Implementations;

namespace Nagi.WinUI;

/// <summary>
///     The main shell of the application, hosting navigation, content, and the media player.
///     This page also provides the custom title bar elements to the main window.
/// </summary>
public sealed partial class MainPage : UserControl, ICustomTitleBarProvider
{
    private const double VolumeChangeStep = 5.0;
    private int _queueDragVersion;
    private NowPlayingIndicatorBinder? _queuePlayingBinder;
    private NowPlayingIndicatorBinder? _historyPlayingBinder;

    private void QueueListView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.OfType<QueueEntry>().Any(entry => entry.Id == ViewModel.CurrentPlayingTrack?.Id))
        {
            e.Cancel = true;
            return;
        }
        _queueDragVersion = ViewModel.SidebarQueue.Version;
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void QueueListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs e)
    {
        if (e.DropResult == DataPackageOperation.Move && e.Items.FirstOrDefault() is QueueEntry entry)
            ViewModel.SidebarQueue.CompleteReorder(entry, _queueDragVersion);
    }

    private async void QueueEntry_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: QueueEntry entry })
        {
            e.Handled = true;
            await PlayQueueEntryAsync(entry);
        }
    }

    private async void QueueEntry_Play(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: QueueEntry entry }) await PlayQueueEntryAsync(entry);
    }

    private async Task PlayQueueEntryAsync(QueueEntry entry)
    {
        try { await ViewModel.SidebarQueue.PlayAsync(entry); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to play queue entry."); }
    }

    private void QueueEntry_MoveUp(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: QueueEntry entry }) ViewModel.SidebarQueue.Move(entry, -1);
    }

    private void Queue_Retry(object sender, RoutedEventArgs e) => ViewModel.SidebarQueue.Refresh(reloadMetadata: true);

    private void QueueEntry_MoveDown(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: QueueEntry entry }) ViewModel.SidebarQueue.Move(entry, 1);
    }

    private async void QueueEntry_Remove(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: QueueEntry entry }) return;
        try { await ViewModel.SidebarQueue.RemoveAsync(entry); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to remove queue entry."); }
    }

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

    private readonly Dictionary<string, Type> _pages = new()
    {
        { "Library", typeof(LibraryPage) },
        { "Favorites", typeof(LibraryPage) },
        { "Folders", typeof(FolderPage) },
        { "Playlists", typeof(PlaylistPage) },
        { "Settings", typeof(SettingsPage) },
        { "Artists", typeof(ArtistPage) },
        { "Albums", typeof(AlbumPage) },
        { "Genres", typeof(GenrePage) },
        { "Insights", typeof(InsightsPage) }
    };

    private readonly IUISettingsService _settingsService;
    private readonly TransparencyEffectsMonitor _transparencyEffects;
    private readonly IThemeService _themeService;
    private readonly IWin32InteropService _win32InteropService;

    private SplitViewContentMotion? _queueContentMotion;
    private SplitViewContentMotion? _navigationContentMotion;

    private bool _isPlayerAnimationEnabled = true;
    private bool _isPointerOverPlayer;
    private readonly DispatcherTimer _hoverTimer = new();
    private bool _pendingPointerOverPlayer;

    // A flag to prevent re-entrant navigation while the selection is being updated programmatically.
    private bool _isUpdatingNavViewSelection;

    private bool _isUnloaded;

    private ElementTheme _lastKnownTheme;

    public MainPage(PlayerViewModel viewModel, InsightsViewModel insightsViewModel,
        GlobalOperationViewModel globalOperation, IUISettingsService settingsService, IThemeService themeService,
        IDispatcherService dispatcherService, IWin32InteropService win32InteropService,
        NavigationService navigationService, TransparencyEffectsMonitor transparencyEffects, ILogger<MainPage> logger)
    {
        ViewModel = viewModel;
        InsightsVm = insightsViewModel;
        GlobalOperation = globalOperation;
        _settingsService = settingsService;
        _transparencyEffects = transparencyEffects;
        _themeService = themeService;
        _dispatcherService = dispatcherService;
        _win32InteropService = win32InteropService;
        _logger = logger;

        InitializeComponent();
        DataContext = ViewModel;

        navigationService.Initialize(ContentFrame);
        _hoverTimer.Tick += OnHoverTimerTick;

        Loaded += OnMainPageLoaded;
        Unloaded += OnMainPageUnloaded;
    }

    public PlayerViewModel ViewModel { get; }
    public InsightsViewModel InsightsVm { get; }
    public GlobalOperationViewModel GlobalOperation { get; }

    public TitleBar GetAppTitleBarElement()
    {
        return AppTitleBar;
    }

    private void HandleNavigation(bool isSettings, object? selectedItem, NavigationTransitionInfo transitionInfo)
    {
        if (isSettings)
        {
            if (ContentFrame.CurrentSourcePageType != typeof(SettingsPage))
                ContentFrame.Navigate(typeof(SettingsPage), null, transitionInfo);
            return;
        }

        if (selectedItem is NavigationViewItem { Tag: string tag } && _pages.TryGetValue(tag, out var pageType))
        {
            var favorites = tag == "Favorites";
            if (ContentFrame.CurrentSourcePageType != pageType ||
                ContentFrame.Content is LibraryPage library && library.ViewModel.ShowFavoritesOnly != favorites)
                ContentFrame.Navigate(pageType, favorites ? "Favorites" : null, transitionInfo);
        }
    }

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

    private void LyricsButton_Click(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.CurrentSourcePageType == typeof(LyricsPage))
            TryGoBack();
        else
            ContentFrame.Navigate(typeof(LyricsPage));
    }

    private void Queue_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
            e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private void QueueButton_DragEnter(object sender, DragEventArgs e)
    {
        Queue_DragOver(sender, e);
        if (e.AcceptedOperation == DataPackageOperation.Copy)
            ViewModel.IsQueuePanelOpen = true;
    }

    private void QueuePanel_DragOver(object sender, DragEventArgs e)
    {
        Queue_DragOver(sender, e);
        QueueDropHighlight.Visibility = e.AcceptedOperation == DataPackageOperation.Copy
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void QueuePanel_DragLeave(object sender, DragEventArgs e) =>
        QueueDropHighlight.Visibility = Visibility.Collapsed;

    private void ContentRegion_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        QueueSplitView.OpenPaneLength = Math.Min(332, Math.Max(0, e.NewSize.Width - 12));
        var overlay = e.NewSize.Width < 760;
        QueueSplitView.DisplayMode = overlay ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
        VisualStateManager.GoToState(this, overlay ? "QueuePaneOverlay" : "QueuePaneInline", false);
    }

    private async void Queue_Drop(object sender, DragEventArgs e)
    {
        QueueDropHighlight.Visibility = Visibility.Collapsed;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        e.Handled = true;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            await ViewModel.AddFilesToQueueAsync(items
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => item.Path));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add dropped files to the playback queue.");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void UpdateNavViewSelection(Type? currentPageType, bool? favorites = null)
    {
        var wasUpdating = _isUpdatingNavViewSelection;
        _isUpdatingNavViewSelection = true;
        try
        {
            if (currentPageType == typeof(SettingsPage))
            {
                NavView.SelectedItem = NavView.SettingsItem;
                return;
            }

            var tag = _pages.FirstOrDefault(p => p.Value == currentPageType).Key;
            if (currentPageType == typeof(LibraryPage))
                tag = (favorites ?? (ContentFrame.Content as LibraryPage)?.ViewModel.ShowFavoritesOnly) == true ? "Favorites" : "Library";
            if (tag is null && currentPageType is not null)
                _detailPageToParentTagMap.TryGetValue(currentPageType, out tag);
            NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>()
                .FirstOrDefault(item => Equals(item.Tag, tag));
        }
        finally
        {
            _isUpdatingNavViewSelection = wasUpdating;
        }
    }

    private Task ApplyDynamicThemeForCurrentTrackAsync()
    {
        var song = ViewModel.CurrentPlayingTrack;
        return _themeService.ApplyDynamicThemeFromSwatchesAsync(song?.LightSwatchId, song?.DarkSwatchId);
    }

    private bool ShouldExpandPlayer =>
        !_isPlayerAnimationEnabled || _isPointerOverPlayer || ViewModel.IsQueuePanelOpen ||
        ViewModel.CurrentPlayingTrack != null && ViewModel.IsPlaying;

    private void UpdatePlayerVisualState(bool useTransitions = true)
    {
        var shouldBeExpanded = ShouldExpandPlayer;
        var stateName = shouldBeExpanded ? "PlayerExpanded" : "PlayerCollapsed";

        useTransitions &= CompositionAnimationHelper.AreAnimationsEnabled;
        VisualStateManager.GoToState(this, stateName, useTransitions);
        if (!useTransitions) PlayerStateGroup.CurrentState?.Storyboard?.SkipToFill();

        var targetOpacity = shouldBeExpanded ? 1f : 0f;
        if (useTransitions)
        {
            var duration = shouldBeExpanded ? 350 : 150;
            var delay = shouldBeExpanded ? 100 : 0;

            CompositionAnimationHelper.AnimateOpacity(SeekBarGrid, targetOpacity, duration, delay);
            CompositionAnimationHelper.AnimateOpacity(ArtistNameHyperlink, targetOpacity, duration, delay);
            CompositionAnimationHelper.AnimateOpacity(SecondaryControlsContainer, targetOpacity, duration, delay);
        }
        else
        {
            CompositionAnimationHelper.SetOpacityImmediate(SeekBarGrid, targetOpacity);
            CompositionAnimationHelper.SetOpacityImmediate(ArtistNameHyperlink, targetOpacity);
            CompositionAnimationHelper.SetOpacityImmediate(SecondaryControlsContainer, targetOpacity);
        }
    }

    private async Task PopulateNavigationAsync()
    {
        var navItems = await _settingsService.GetNavigationItemsAsync();
        if (_isUnloaded) return;
        var wasUpdating = _isUpdatingNavViewSelection;
        _isUpdatingNavViewSelection = true;
        try
        {
            NavView.MenuItems.Clear();
            foreach (var item in navItems.Where(i => i.IsEnabled))
            {
                var resourceKey = $"NavItem_{item.Tag}";
                NavView.MenuItems.Add(new NavigationViewItem
                {
                    Content = Strings.ResourceManager.GetString(resourceKey) ?? resourceKey,
                    Tag = item.Tag,
                    Icon = new FontIcon { Glyph = item.IconGlyph }
                });
            }
            UpdateNavViewSelection(ContentFrame.CurrentSourcePageType);
        }
        finally
        {
            _isUpdatingNavViewSelection = wasUpdating;
        }
    }

    private async void OnMainPageLoaded(object sender, RoutedEventArgs e)
    {
        _isUnloaded = false;
        _queueContentMotion ??= SplitViewContentMotion.Attach(QueueSplitView);
        NavView.ApplyTemplate();
        if (VisualTreeHelper.GetChildrenCount(NavView) > 0 &&
            (VisualTreeHelper.GetChild(NavView, 0) as FrameworkElement)?.FindName("RootSplitView") is SplitView navigationSplitView)
            _navigationContentMotion ??= SplitViewContentMotion.Attach(navigationSplitView);
        _queuePlayingBinder ??= new NowPlayingIndicatorBinder(
            QueueListView, ViewModel,
            (Brush)Application.Current.Resources["AppPrimaryColorBrush"],
            item => (item as QueueEntry)?.Id);
        _queuePlayingBinder.Refresh();
        _historyPlayingBinder ??= new NowPlayingIndicatorBinder(
            HistoryListView, ViewModel,
            (Brush)Application.Current.Resources["AppPrimaryColorBrush"],
            item => (item as RecentListenEntry)?.Listen.SongId);
        _historyPlayingBinder.Refresh();
        if (NavView.SettingsItem is NavigationViewItem settingsItem)
        {
            settingsItem.Content = Strings.NavItem_Settings;
        }

        try
        {
            _lastKnownTheme = ActualTheme;
            ActualThemeChanged += OnActualThemeChanged;
            ContentFrame.Navigated += OnContentFrameNavigated;
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            CompositionAnimationHelper.AnimationsEnabledChanged += OnAnimationsEnabledChanged;
            _settingsService.PlayerAnimationSettingChanged += OnPlayerAnimationSettingChanged;
            _settingsService.NavigationSettingsChanged += OnNavigationSettingsChanged;
            _transparencyEffects.Changed += OnTransparencyEffectsChanged;
            _settingsService.PlayerDesignSettingsChanged += OnPlayerDesignSettingsChanged;
            AddHandler(PointerPressedEvent, new PointerEventHandler(OnGlobalPointerPressed), true);
            KeyDown += OnSelectionKeyDown;

            var brushTask = SetPlatformSpecificBrushAsync();
            var navTask = PopulateNavigationAsync();
            var animTask = _settingsService.GetPlayerAnimationEnabledAsync();
            var paneTask = RestorePaneStateAsync();

            await Task.WhenAll(brushTask, navTask, animTask, paneTask);
            if (_isUnloaded) return;

            _isPlayerAnimationEnabled = await animTask;

            if (ContentFrame.CurrentSourcePageType is null)
                NavView.SelectedItem = NavView.MenuItems.FirstOrDefault() ?? NavView.SettingsItem;

            UpdateNavViewSelection(ContentFrame.CurrentSourcePageType);

            UpdatePlayerVisualState(false);
            await ApplyDynamicThemeForCurrentTrackAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during MainPage initialization");
        }
    }

    private void OnMainPageUnloaded(object sender, RoutedEventArgs e)
    {
        _queuePlayingBinder?.Dispose();
        _queuePlayingBinder = null;
        _historyPlayingBinder?.Dispose();
        _historyPlayingBinder = null;
        _isUnloaded = true;
        _hoverTimer.Stop();
        _isPointerOverPlayer = false;
        ActualThemeChanged -= OnActualThemeChanged;
        ContentFrame.Navigated -= OnContentFrameNavigated;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        CompositionAnimationHelper.AnimationsEnabledChanged -= OnAnimationsEnabledChanged;
        _settingsService.PlayerAnimationSettingChanged -= OnPlayerAnimationSettingChanged;
        _settingsService.NavigationSettingsChanged -= OnNavigationSettingsChanged;
        _transparencyEffects.Changed -= OnTransparencyEffectsChanged;
        _settingsService.PlayerDesignSettingsChanged -= OnPlayerDesignSettingsChanged;

        // Dispose the tray icon control to prevent "Exception Processing Message 0xc0000005" errors on exit.
        AppTrayIconHost?.Dispose();

        RemoveHandler(PointerPressedEvent, (PointerEventHandler)OnGlobalPointerPressed);
        KeyDown -= OnSelectionKeyDown;
    }

    /// <summary>
    ///     Sets the background brush for the floating player based on user settings and OS capabilities.
    ///     Handles switching between Acrylic (glass) and Solid (opaque) materials.
    /// </summary>
    private async Task SetPlatformSpecificBrushAsync()
    {
        var isAcrylicEnabled = _transparencyEffects.IsEnabled;
        var material = await _settingsService.GetPlayerBackgroundMaterialAsync();
        if (_isUnloaded) return;
        var brushKey = !isAcrylicEnabled || material == PlayerBackgroundMaterial.Solid
            ? "PlayerBackgroundBrush"
            : _win32InteropService.IsWindows11OrNewer ? "Win11AcrylicBrush" : "Win10AcrylicBrush";
        FloatingPlayerContainer.Background = (Brush)Application.Current.Resources[brushKey];
    }

    private void OnPlayerDesignSettingsChanged()
    {
        _ = UpdateUiAsync(async () =>
        {
            await SetPlatformSpecificBrushAsync();
            if (!_isUnloaded) await _themeService.ReapplyCurrentDynamicThemeAsync();
        });
    }

    private void OnTransparencyEffectsChanged(bool isEnabled)
    {
        _ = UpdateUiAsync(SetPlatformSpecificBrushAsync);
    }

    private void OnAnimationsEnabledChanged(Windows.UI.ViewManagement.UISettings sender,
        Windows.UI.ViewManagement.UISettingsAnimationsEnabledChangedEventArgs args)
    {
        _dispatcherService.TryEnqueue(() =>
        {
            if (_isUnloaded) return;
            UpdatePlayerVisualState(false);
            if (!CompositionAnimationHelper.AreAnimationsEnabled)
                PlayerVisibilityStates.CurrentState?.Storyboard?.SkipToFill();
        });
    }

    private void OnPlayerAnimationSettingChanged(bool isEnabled)
    {
        _dispatcherService.TryEnqueue(() =>
        {
            if (_isUnloaded) return;
            _isPlayerAnimationEnabled = isEnabled;
            UpdatePlayerVisualState(false);
        });
    }

    private void OnNavigationSettingsChanged()
    {
        _ = UpdateUiAsync(PopulateNavigationAsync);
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        if (ActualTheme == _lastKnownTheme) return;
        _lastKnownTheme = ActualTheme;
        _ = UpdateUiAsync(_themeService.ReapplyCurrentDynamicThemeAsync);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(PlayerViewModel.CurrentPlayingTrack) or
            nameof(PlayerViewModel.IsPlaying) or nameof(PlayerViewModel.IsQueuePanelOpen))) return;

        _ = UpdateUiAsync(async () =>
        {
            UpdatePlayerVisualState();
            if (e.PropertyName == nameof(PlayerViewModel.CurrentPlayingTrack))
                await ApplyDynamicThemeForCurrentTrackAsync();
        });
    }

    private async Task UpdateUiAsync(Func<Task> update)
    {
        try
        {
            await _dispatcherService.EnqueueAsync(() => _isUnloaded ? Task.CompletedTask : update());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update the main page.");
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

    private void OnContentFrameNavigated(object sender, NavigationEventArgs e)
    {
        _logger.LogDebug("Navigated to {PageName}.", e.SourcePageType.Name);
        try
        {
            var isDetailPage = _detailPageToParentTagMap.ContainsKey(e.SourcePageType);
            var isLyricsPage = e.SourcePageType == typeof(LyricsPage);
            ViewModel.IsLyricsPageActive = isLyricsPage;

            AppTitleBar.IsBackButtonVisible = ContentFrame.CanGoBack && (isDetailPage || isLyricsPage);

            UpdateNavViewSelection(e.SourcePageType, e.Parameter is "Favorites");

            var isSettingsPage = e.SourcePageType == typeof(SettingsPage);
            // Keep destination bounds stable while player space changes, so WinUI can finish the outgoing fade.
            var pageHeightAnimation = isSettingsPage ? HiddenPageHeightAnimation : VisiblePageHeightAnimation;
            var reservedHeight = isSettingsPage ? 0 : ShouldExpandPlayer ? 132 : 80;
            pageHeightAnimation.From = pageHeightAnimation.To = Math.Max(0, ContentRegion.ActualHeight - reservedHeight);
            if (isSettingsPage) PlayerSpaceHideAnimation.From = PlayerContentSpace.ActualHeight;
            var stateName = isSettingsPage ? "PlayerHidden" : "PlayerVisible";
            VisualStateManager.GoToState(this, stateName, CompositionAnimationHelper.AreAnimationsEnabled);
            if (!CompositionAnimationHelper.AreAnimationsEnabled) PlayerVisibilityStates.CurrentState?.Storyboard?.SkipToFill();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing OnContentFrameNavigated");
        }
    }

    private void FloatingPlayerContainer_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        SchedulePlayerHover(true);

    private void FloatingPlayerContainer_PointerExited(object sender, PointerRoutedEventArgs e) =>
        SchedulePlayerHover(false);

    private void SchedulePlayerHover(bool isOverPlayer)
    {
        _hoverTimer.Stop();
        _pendingPointerOverPlayer = isOverPlayer;
        // Ignore brief grazes across the transparent hit zone and rapid boundary crossings.
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(isOverPlayer ? 80 : 120);
        _hoverTimer.Start();
    }

    private void OnHoverTimerTick(object? sender, object e)
    {
        _hoverTimer.Stop();
        if (_isUnloaded) return;
        _isPointerOverPlayer = _pendingPointerOverPlayer;
        UpdatePlayerVisualState();
    }

    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
        _ = SavePaneStateAsync(NavView.IsPaneOpen);
    }

    private void AppTitleBar_BackRequested(TitleBar sender, object args)
    {
        TryGoBack();
    }

    private void MediaSeekerSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        ViewModel.IsUserDraggingSlider = true;
    }

    private void MediaSeekerSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
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

    private async Task SavePaneStateAsync(bool isOpen)
    {
        try
        {
            if (await _settingsService.GetRememberPaneStateEnabledAsync())
                await _settingsService.SetLastPaneOpenAsync(isOpen);
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Failed to save navigation pane state.");
        }
    }

    private void VolumeSliderContainer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var pointerPoint = e.GetCurrentPoint((UIElement)sender);
        var delta = pointerPoint.Properties.MouseWheelDelta;

        ViewModel.CurrentVolume = Math.Clamp(ViewModel.CurrentVolume + Math.Sign(delta) * VolumeChangeStep, 0, 100);

        e.Handled = true;
    }

    private async Task RestorePaneStateAsync()
    {
        try
        {
            if (await _settingsService.GetRememberPaneStateEnabledAsync())
            {
                var savedState = await _settingsService.GetLastPaneOpenAsync();
                if (!_isUnloaded && savedState.HasValue) NavView.IsPaneOpen = savedState.Value;
            }
        }
        catch (Exception ex)
        {
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
        else if (properties.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.LeftButtonPressed &&
                 VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Count == 0)
        {
            DismissListSelection(e.OriginalSource as DependencyObject);
        }
    }

    private void OnSelectionKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || e.Handled ||
            VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Count != 0) return;
        for (var element = e.OriginalSource as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
            if (element is TextBox or PasswordBox or RichEditBox) return;

        e.Handled = DismissListSelection(null);
    }

    private bool DismissListSelection(DependencyObject? source)
    {
        ListView? clickedList = null;
        var clickedItem = false;
        var clickedScrollBar = false;
        var page = ContentFrame.Content as FrameworkElement;
        for (var element = source; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is ListViewItem) clickedItem = true;
            if (element is ScrollBar) clickedScrollBar = true;
            if (element is ListView list) clickedList = list;
        }

        var cleared = false;
        if (page is SongListPage songListPage &&
            page.DataContext is SongListViewModelBase { HasSelectedSongs: true } &&
            !(clickedList is not null && IsWithin(clickedList, page) && (clickedItem || clickedScrollBar)))
        {
            cleared = true;
            songListPage.ClearSelection();
        }

        if (QueueListView.SelectedItem is not null &&
            !(clickedList == QueueListView && (clickedItem || clickedScrollBar)))
        {
            QueueListView.SelectedItem = null;
            cleared = true;
        }
        return cleared;
    }

    private static bool IsWithin(DependencyObject element, DependencyObject parent)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current == parent) return true;
        return false;
    }

    // ── Insights "See All" overlay handlers ──

    private void OnCloseSeeAll(object sender, RoutedEventArgs e)
    {
        VisualStateManager.GoToState(this, "SeeAllSearchCollapsed", false);
        InsightsVm.CloseSeeAllCommand.Execute(null);
    }

    private void OnSeeAllPanelTapped(object sender, TappedRoutedEventArgs e) =>
        e.Handled = true;

    private void OnSeeAllSearchToggleButtonClick(object sender, RoutedEventArgs e)
    {
        if (SeeAllSearchStates.CurrentState?.Name == "SeeAllSearchExpanded")
            CollapseSeeAllSearch();
        else
        {
            VisualStateManager.GoToState(this, "SeeAllSearchExpanded", false);
            SeeAllSearchTextBox.Focus(FocusState.Programmatic);
        }
    }

    private void CollapseSeeAllSearch()
    {
        InsightsVm.SearchTerm = string.Empty;
        VisualStateManager.GoToState(this, "SeeAllSearchCollapsed", false);
        SeeAllSearchToggleButton.Focus(FocusState.Programmatic);
    }

    private void OnSeeAllSortByPlayCountClick(object sender, RoutedEventArgs e) =>
        InsightsVm.SetCurrentCategoryMetric(SortMetric.PlayCount);

    private void OnSeeAllSortByDurationClick(object sender, RoutedEventArgs e) =>
        InsightsVm.SetCurrentCategoryMetric(SortMetric.Duration);

    private void OnSeeAllSearchTextBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            CollapseSeeAllSearch();
            e.Handled = true;
        }
    }

}

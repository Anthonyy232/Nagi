using Windows.System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows.Input;
using Windows.ApplicationModel.DataTransfer;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Core.Platform;
using Nagi.Core.Settings;
using Nagi.Presentation.Lists;
using Nagi.Presentation.Models;
using Nagi.Presentation.Player;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services;
using Nagi.Presentation.ViewModels;
using Nagi.WinUI.Controls;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Pages;
using Nagi.WinUI.Services.Implementations;

namespace Nagi.WinUI;

public sealed partial class MainPage : UserControl, ICustomTitleBarProvider
{
    private int _queueDragVersion;
    private NowPlayingIndicatorBinder? _queuePlayingBinder;
    private NowPlayingIndicatorBinder? _historyPlayingBinder;

    private void QueueListView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (!e.Items.OfType<QueueEntry>().All(ViewModel.SidebarQueue.CanMove))
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

    private void QueueEntry_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) =>
        PlayTappedEntry(sender, e, ViewModel.PlayQueueEntryCommand);

    private void HistoryEntry_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) =>
        PlayTappedEntry(sender, e, ViewModel.PlayHistoryNowCommand);

    private static void PlayTappedEntry(object sender, DoubleTappedRoutedEventArgs e, ICommand play)
    {
        if (sender is not FrameworkElement { Tag: { } entry }) return;
        e.Handled = true;
        play.Execute(entry);
    }

    // Read by the layout and the player storyboards in XAML as well as by the code here.
    private double ExpandedPlayerHeight => 132;
    private double CollapsedPlayerHeight => 80;
    private double QueuePaneWidth => 332;

    private readonly IDispatcherService _dispatcherService;
    private readonly NavigationService _navigationService;
    private readonly ILogger<MainPage> _logger;
    private readonly ISettingsService _settingsService;

    private SplitViewContentMotion? _queueContentMotion;
    private SplitViewContentMotion? _navigationContentMotion;

    private bool _isPointerOverPlayer;
    private readonly DispatcherTimer _hoverTimer = new();
    private bool _pendingPointerOverPlayer;

    // A flag to prevent re-entrant navigation while the selection is being updated programmatically.
    private bool _isUpdatingNavViewSelection;

    private bool _isUnloaded;

    public MainPage(PlayerViewModel viewModel,
        GlobalOperationViewModel globalOperation, ISettingsService settingsService,
        IDispatcherService dispatcherService,
        NavigationService navigationService, ILogger<MainPage> logger)
    {
        ViewModel = viewModel;
        GlobalOperation = globalOperation;
        _settingsService = settingsService;
        _dispatcherService = dispatcherService;
        _navigationService = navigationService;
        _logger = logger;

        InitializeComponent();
        DataContext = ViewModel;
        PlayerInputHelper.AttachSeekSlider(MediaSeekerSlider, ViewModel);

        navigationService.Initialize(ContentFrame);
        _hoverTimer.Tick += OnHoverTimerTick;

        Loaded += OnMainPageLoaded;
        Unloaded += OnMainPageUnloaded;
    }

    public PlayerViewModel ViewModel { get; }
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

        if (selectedItem is NavigationViewItem { Tag: string tag } &&
            AppDestinations.TryGetMenuPage(tag, out var pageType, out var parameter) &&
            (ContentFrame.CurrentSourcePageType != pageType || !Equals(_navigationService.CurrentParameter, parameter)))
            ContentFrame.Navigate(pageType, parameter, transitionInfo);
    }

    private void TryGoBack()
    {
        if (ContentFrame.CanGoBack) ContentFrame.GoBack();
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
        QueueSplitView.OpenPaneLength = Math.Min(QueuePaneWidth, Math.Max(0, e.NewSize.Width - 12));
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
            await ViewModel.AddFilesToQueueAsync(items.Select(item => item.Path));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read the dropped files.");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void UpdateNavViewSelection()
    {
        var wasUpdating = _isUpdatingNavViewSelection;
        _isUpdatingNavViewSelection = true;
        try
        {
            if (ContentFrame.CurrentSourcePageType == typeof(SettingsPage))
            {
                NavView.SelectedItem = NavView.SettingsItem;
                return;
            }

            var tag = AppDestinations.GetMenuTag(ContentFrame.CurrentSourcePageType, _navigationService.CurrentParameter);
            NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>()
                .FirstOrDefault(item => Equals(item.Tag, tag));
        }
        finally
        {
            _isUpdatingNavViewSelection = wasUpdating;
        }
    }

    private bool ShouldExpandPlayer =>
        !_settingsService.Get(AppSettings.PlayerAnimation) || _isPointerOverPlayer || ViewModel.IsQueuePanelOpen ||
        ViewModel.CurrentPlayingTrack != null && ViewModel.IsPlaying;

    private void UpdatePlayerVisualState(bool useTransitions = true)
    {
        var shouldBeExpanded = ShouldExpandPlayer;
        var stateName = shouldBeExpanded ? "PlayerExpanded" : "PlayerCollapsed";

        useTransitions &= SystemVisualSettings.AnimationsEnabled;
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

    private void PopulateNavigation()
    {
        var navItems = ViewModel.GetVisibleNavigationItems();
        var wasUpdating = _isUpdatingNavViewSelection;
        _isUpdatingNavViewSelection = true;
        try
        {
            NavView.MenuItems.Clear();
            foreach (var item in navItems)
            {
                NavView.MenuItems.Add(new NavigationViewItem
                {
                    Content = item.DisplayName,
                    Tag = item.Tag,
                    Icon = new FontIcon { Glyph = AppIconGlyphs.ToGlyph(item.Icon) }
                });
            }
            UpdateNavViewSelection();
        }
        finally
        {
            _isUpdatingNavViewSelection = wasUpdating;
        }
    }

    private void OnMainPageLoaded(object sender, RoutedEventArgs e)
    {
        // The page is unloaded only when the window closes or swaps it out, which ends it, so it loads once.
        Loaded -= OnMainPageLoaded;
        _queueContentMotion = SplitViewContentMotion.Attach(QueueSplitView);
        NavView.ApplyTemplate();
        if (VisualTreeHelper.GetChildrenCount(NavView) > 0 &&
            (VisualTreeHelper.GetChild(NavView, 0) as FrameworkElement)?.FindName("RootSplitView") is SplitView navigationSplitView)
            _navigationContentMotion = SplitViewContentMotion.Attach(navigationSplitView);
        _queuePlayingBinder = new NowPlayingIndicatorBinder(QueueListView, ViewModel, item => (item as QueueEntry)?.Id);
        _historyPlayingBinder = new NowPlayingIndicatorBinder(HistoryListView, ViewModel,
            item => (item as RecentListenEntry)?.Listen.SongId);
        if (NavView.SettingsItem is NavigationViewItem settingsItem)
        {
            settingsItem.Content = Strings.NavItem_Settings;
        }

        ContentFrame.Navigated += OnContentFrameNavigated;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        SystemVisualSettings.AnimationsEnabledChanged += OnAnimationsEnabledChanged;
        _settingsService.SettingChanged += OnSettingChanged;
        SystemVisualSettings.TransparencyEffectsEnabledChanged += OnTransparencyEffectsChanged;
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnGlobalPointerPressed), true);
        KeyDown += OnSelectionKeyDown;

        SetPlatformSpecificBrush();
        PopulateNavigation();
        if (ViewModel.GetRememberedPaneOpen() is { } isPaneOpen) NavView.IsPaneOpen = isPaneOpen;

        if (ContentFrame.CurrentSourcePageType is null)
            NavView.SelectedItem = NavView.MenuItems.FirstOrDefault() ?? NavView.SettingsItem;

        UpdateNavViewSelection();

        UpdatePlayerVisualState(false);
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
        ContentFrame.Navigated -= OnContentFrameNavigated;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        SystemVisualSettings.AnimationsEnabledChanged -= OnAnimationsEnabledChanged;
        _settingsService.SettingChanged -= OnSettingChanged;
        SystemVisualSettings.TransparencyEffectsEnabledChanged -= OnTransparencyEffectsChanged;

        // Dispose the tray icon control to prevent "Exception Processing Message 0xc0000005" errors on exit.
        AppTrayIconHost?.Dispose();

        RemoveHandler(PointerPressedEvent, (PointerEventHandler)OnGlobalPointerPressed);
        KeyDown -= OnSelectionKeyDown;
    }

    private void SetPlatformSpecificBrush()
    {
        var isAcrylicEnabled = SystemVisualSettings.TransparencyEffectsEnabled;
        var material = _settingsService.Get(AppSettings.PlayerBackgroundMaterial);
        var brushKey = !isAcrylicEnabled || material == PlayerBackgroundMaterial.Solid
            ? "PlayerBackgroundBrush"
            : NativeMethods.IsWindows11OrNewer ? "Win11AcrylicBrush" : "Win10AcrylicBrush";
        FloatingPlayerContainer.Background = (Brush)Application.Current.Resources[brushKey];
    }

    private void OnSettingChanged(Setting setting)
    {
        if (setting == AppSettings.PlayerBackgroundMaterial)
            UpdateUi(SetPlatformSpecificBrush);
        else if (setting == AppSettings.PlayerAnimation)
            UpdateUi(() => UpdatePlayerVisualState(false));
        else if (setting == AppSettings.NavigationItems)
            UpdateUi(PopulateNavigation);
    }

    private void OnTransparencyEffectsChanged(bool isEnabled) => SetPlatformSpecificBrush();

    private void OnAnimationsEnabledChanged(bool isEnabled)
    {
        UpdatePlayerVisualState(false);
        if (!isEnabled) PlayerVisibilityStates.CurrentState?.Storyboard?.SkipToFill();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.CurrentPlayingTrack) or nameof(PlayerViewModel.IsPlaying)
            or nameof(PlayerViewModel.IsQueuePanelOpen))
            UpdateUi(() => UpdatePlayerVisualState());
    }

    private void UpdateUi(Action update) => _dispatcherService.TryEnqueue(() =>
    {
        if (!_isUnloaded) update();
    });

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
        var isDetailPage = AppDestinations.IsDetailPage(e.SourcePageType);
        var isLyricsPage = e.SourcePageType == typeof(LyricsPage);
        ViewModel.IsLyricsPageActive = isLyricsPage;

        AppTitleBar.IsBackButtonVisible = ContentFrame.CanGoBack && (isDetailPage || isLyricsPage);

        UpdateNavViewSelection();

        var isSettingsPage = e.SourcePageType == typeof(SettingsPage);
        // Keep destination bounds stable while player space changes, so WinUI can finish the outgoing fade.
        var pageHeightAnimation = isSettingsPage ? HiddenPageHeightAnimation : VisiblePageHeightAnimation;
        var reservedHeight = isSettingsPage ? 0 : ShouldExpandPlayer ? ExpandedPlayerHeight : CollapsedPlayerHeight;
        pageHeightAnimation.From = pageHeightAnimation.To = Math.Max(0, ContentRegion.ActualHeight - reservedHeight);
        if (isSettingsPage) PlayerSpaceHideAnimation.From = PlayerContentSpace.ActualHeight;
        var stateName = isSettingsPage ? "PlayerHidden" : "PlayerVisible";
        VisualStateManager.GoToState(this, stateName, SystemVisualSettings.AnimationsEnabled);
        if (!SystemVisualSettings.AnimationsEnabled) PlayerVisibilityStates.CurrentState?.Storyboard?.SkipToFill();
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
        ViewModel.RememberPaneOpen(NavView.IsPaneOpen);
    }

    private void AppTitleBar_BackRequested(TitleBar sender, object args)
    {
        TryGoBack();
    }

    private void VolumeSliderContainer_PointerWheelChanged(object sender, PointerRoutedEventArgs e) =>
        PlayerInputHelper.StepVolumeWithWheel(sender, e, ViewModel);

    // Registered with handledEventsToo, so the mouse back button and click-away work even where a child control
    // handled the press.
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
        if (e.Key != VirtualKey.Escape || e.Handled || IsShortcutBlocked(e.OriginalSource as DependencyObject)) return;

        e.Handled = DismissListSelection(null);
    }

    // Page-wide keyboard shortcuts give way to text input and to open menus, flyouts and dialogs.
    private bool IsShortcutBlocked(DependencyObject? focusedElement)
    {
        if (VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Count != 0) return true;
        for (var element = focusedElement; element is not null; element = VisualTreeHelper.GetParent(element))
            if (element is TextBox or PasswordBox or RichEditBox) return true;
        return false;
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
            !(clickedList?.FindAscendant<FrameworkElement>(ancestor => ancestor == page) is not null &&
              (clickedItem || clickedScrollBar)))
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
}

using Windows.System;
using System.ComponentModel;
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
using Nagi.Core.Platform;
using Nagi.Core.Settings;
using Nagi.Presentation.Insights;
using Nagi.Presentation.Player;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Settings;
using Nagi.Presentation.Shell;
using Nagi.Presentation.Theme;
using Nagi.WinUI.Controls;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Pages;
using Nagi.WinUI.Services.Implementations;
using Nagi.WinUI.Styles;

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
        if (e.DropResult == DataPackageOperation.Move && e.Items is [QueueEntry entry, ..])
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


    private bool _isPointerOverPlayer;
    private readonly DispatcherTimer _hoverTimer = new();
    private bool _pendingPointerOverPlayer;

    private bool _isUnloaded;
    private readonly TrayIconViewModel _trayIconViewModel;
    private TrayIconUserControl? _trayIcon;

    public MainPage(PlayerViewModel viewModel,
        GlobalOperationViewModel globalOperation, ISettingsService settingsService,
        IDispatcherService dispatcherService,
        NavigationService navigationService, InsightsViewModel insights, TrayIconViewModel trayIcon,
        ILogger<MainPage> logger)
    {
        ViewModel = viewModel;
        GlobalOperation = globalOperation;
        _settingsService = settingsService;
        _dispatcherService = dispatcherService;
        _navigationService = navigationService;
        _logger = logger;

        InitializeComponent();
        PlayerInputHelper.AttachSeekSlider(MediaSeekerSlider, ViewModel);
        _trayIconViewModel = trayIcon;
        // Created when first shown: it takes about 70 ms, and a launch into a visible window doesn't need it.
        if (trayIcon.IsTrayIconVisible) AddTrayIcon();
        else trayIcon.PropertyChanged += OnTrayIconPropertyChanged;
        // Last, so it covers the whole window, player included.
        RootGrid.Children.Add(new InsightsSeeAllOverlay(insights));

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

    public static readonly DependencyProperty IsLyricsPageShownProperty =
        DependencyProperty.Register(nameof(IsLyricsPageShown), typeof(bool), typeof(MainPage), new PropertyMetadata(false));

    // Read by the lyrics button's template, which can only reach the page through a binding.
    public bool IsLyricsPageShown
    {
        get => (bool)GetValue(IsLyricsPageShownProperty);
        private set => SetValue(IsLyricsPageShownProperty, value);
    }

    private void HandleNavigation(bool isSettings, object? selectedItem, NavigationTransitionInfo transitionInfo)
    {
        var tag = isSettings ? AppDestinations.SettingsTag : (selectedItem as NavigationViewItem)?.Tag as string;
        if (tag is not null) _navigationService.NavigateToMenu(tag, transitionInfo);
    }

    private void LyricsButton_Click(object sender, RoutedEventArgs e) => _navigationService.ToggleLyrics();

    private void OnFullScreenShortcutInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsShortcutBlocked(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject) ||
            !ViewModel.ToggleFullScreenCommand.CanExecute(null)) return;
        args.Handled = true;
        ViewModel.ToggleFullScreenCommand.Execute(null);
    }

    // Full screen belongs to the lyrics page: entering opens it, and leaving it ends full screen.
    private void UpdateFullScreenState()
    {
        VisualStateManager.GoToState(this, ViewModel.IsFullScreen ? "FullScreen" : "Windowed", false);
        if (ViewModel.IsFullScreen && !IsLyricsPageShown) _navigationService.ToggleLyrics();
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
        var tag = _navigationService.CurrentMenuTag;
        NavView.SelectedItem = tag == AppDestinations.SettingsTag
            ? NavView.SettingsItem
            : NavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(item => Equals(item.Tag, tag));
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
        NavView.MenuItems.Clear();
        foreach (var item in ViewModel.GetVisibleNavigationItems())
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

    private void OnMainPageLoaded(object sender, RoutedEventArgs e)
    {
        // The page is unloaded only when the window closes or swaps it out, which ends it, so it loads once.
        Loaded -= OnMainPageLoaded;
        SplitViewContentMotion.Attach(QueueSplitView);
        NavView.ApplyTemplate();
        if (VisualTreeHelper.GetChildrenCount(NavView) > 0 &&
            (VisualTreeHelper.GetChild(NavView, 0) as FrameworkElement)?.FindName("RootSplitView") is SplitView navigationSplitView)
            SplitViewContentMotion.Attach(navigationSplitView);
        _queuePlayingBinder = new NowPlayingIndicatorBinder(QueueListView, ViewModel, item => (item as QueueEntry)?.Id);
        _historyPlayingBinder = new NowPlayingIndicatorBinder(HistoryListView, ViewModel,
            item => (item as RecentListenEntry)?.Listen.SongId);
        if (NavView.SettingsItem is NavigationViewItem settingsItem)
        {
            settingsItem.Content = Strings.NavItem_Settings;
        }

        _navigationService.Navigated += OnNavigated;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        SystemVisualSettings.AnimationsEnabledChanged += OnAnimationsEnabledChanged;
        _settingsService.SettingChanged += OnSettingChanged;
        SystemVisualSettings.TransparencyEffectsEnabledChanged += OnTransparencyEffectsChanged;
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnGlobalPointerPressed), true);
        KeyDown += OnEscapeKeyDown;

        SetPlatformSpecificBrush();
        PopulateNavigation();
        if (ViewModel.GetRememberedPaneOpen() is { } isPaneOpen) NavView.IsPaneOpen = isPaneOpen;

        if (ContentFrame.CurrentSourcePageType is null)
            HandleNavigation(NavView.MenuItems.Count == 0, NavView.MenuItems.FirstOrDefault(), new EntranceNavigationTransitionInfo());

        UpdatePlayerVisualState(false);
    }

    private void OnTrayIconPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_trayIconViewModel.IsTrayIconVisible) return;
        _trayIconViewModel.PropertyChanged -= OnTrayIconPropertyChanged;
        AddTrayIcon();
    }

    private void AddTrayIcon()
    {
        _trayIcon = new TrayIconUserControl(_trayIconViewModel);
        RootGrid.Children.Insert(0, _trayIcon);
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
        _navigationService.Navigated -= OnNavigated;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        SystemVisualSettings.AnimationsEnabledChanged -= OnAnimationsEnabledChanged;
        _settingsService.SettingChanged -= OnSettingChanged;
        SystemVisualSettings.TransparencyEffectsEnabledChanged -= OnTransparencyEffectsChanged;

        // Dispose the tray icon control to prevent "Exception Processing Message 0xc0000005" errors on exit.
        _trayIconViewModel.PropertyChanged -= OnTrayIconPropertyChanged;
        _trayIcon?.Dispose();

        RemoveHandler(PointerPressedEvent, (PointerEventHandler)OnGlobalPointerPressed);
        KeyDown -= OnEscapeKeyDown;
    }

    private void SetPlatformSpecificBrush()
    {
        var isAcrylicEnabled = SystemVisualSettings.TransparencyEffectsEnabled;
        var material = _settingsService.Get(AppSettings.PlayerBackgroundMaterial);
        var brushKey = !isAcrylicEnabled || material == PlayerBackgroundMaterial.Solid
            ? AppResourceKeys.PlayerBackgroundBrush
            : NativeMethods.IsWindows11OrNewer ? AppResourceKeys.Win11AcrylicBrush : AppResourceKeys.Win10AcrylicBrush;
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
        else if (e.PropertyName == nameof(PlayerViewModel.IsFullScreen))
            UpdateUi(UpdateFullScreenState);
    }

    private void UpdateUi(Action update) => _dispatcherService.TryEnqueue(() =>
    {
        if (!_isUnloaded) update();
    });

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        HandleNavigation(args.IsSettingsInvoked, args.InvokedItemContainer, args.RecommendedNavigationTransitionInfo);
    }

    private void OnNavigated(Type page)
    {
        IsLyricsPageShown = page == typeof(LyricsPage);
        if (!IsLyricsPageShown) ViewModel.ExitFullScreen();
        AppTitleBar.IsBackButtonVisible = _navigationService.ShowsBackButton;
        UpdateNavViewSelection();

        var isSettingsPage = page == typeof(SettingsPage);
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
        _navigationService.GoBack();
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
            _navigationService.GoBack();
            e.Handled = true;
        }
        else if (properties.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.LeftButtonPressed &&
                 VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Count == 0)
        {
            DismissListSelection(e.OriginalSource as DependencyObject);
        }
    }

    private void OnEscapeKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || e.Handled || IsShortcutBlocked(e.OriginalSource as DependencyObject)) return;

        if (ViewModel.IsFullScreen)
        {
            ViewModel.ExitFullScreen();
            e.Handled = true;
        }
        else
        {
            e.Handled = DismissListSelection(null);
        }
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
            !(clickedList?.FindAscendant<FrameworkElement>(ancestor => ancestor == page) is not null &&
              (clickedItem || clickedScrollBar)))
            cleared = songListPage.ClearSelection();

        if (QueueListView.SelectedItem is not null &&
            !(clickedList == QueueListView && (clickedItem || clickedScrollBar)))
        {
            QueueListView.SelectedItem = null;
            cleared = true;
        }
        return cleared;
    }
}

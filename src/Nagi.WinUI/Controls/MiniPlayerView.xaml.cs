using System;
using System.Collections.Generic;
using System.ComponentModel;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.ViewModels;

namespace Nagi.WinUI.Controls;

public sealed partial class MiniPlayerView : UserControl
{
    private readonly Window _parentWindow;
    private bool _isUnloaded;

    public MiniPlayerView(Window parentWindow, PlayerViewModel viewModel)
    {
        InitializeComponent();
        _parentWindow = parentWindow;

        ViewModel = viewModel;
        DataContext = this;

        SubscribeToEvents();
    }

    public PlayerViewModel ViewModel { get; }

    /// <summary>
    ///     The element the parent window should register as its title-bar drag region.
    /// </summary>
    public UIElement TitleBarDragRegion => DragHandle;

    public event EventHandler? RestoreButtonClicked;

    private void SubscribeToEvents()
    {
        RestoreButton.Click += OnRestoreButtonClick;

        HoverDetector.PointerEntered += OnPointerEntered;
        HoverDetector.PointerExited += OnPointerExited;
        HoverDetector.PointerCanceled += OnPointerExited;

        MediaSeekerSlider.SizeChanged += OnInteractiveRegionChanged;
        MediaControlsPanel.SizeChanged += OnInteractiveRegionChanged;
        BackButton.SizeChanged += OnInteractiveRegionChanged;
        RestoreButton.SizeChanged += OnInteractiveRegionChanged;
        QueueListView.SizeChanged += OnInteractiveRegionChanged;
        ArtistTextBlock.SizeChanged += OnInteractiveRegionChanged;
        SizeChanged += OnInteractiveRegionChanged;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isUnloaded = false;
        _parentWindow.Activated += OnWindowActivated;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Initialize hover controls visual to ensure first hover works immediately.
        CompositionAnimationHelper.SetOpacityImmediate(HoverControlsContainer, 0f);

        UpdateInteractiveRegions();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isUnloaded = true;
        _parentWindow.Activated -= OnWindowActivated;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.IsQueueViewVisible))
        {
            DispatcherQueue.TryEnqueue(UpdateInteractiveRegions);
        }
    }

    private void OnInteractiveRegionChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateInteractiveRegions();
    }

    /// <summary>
    ///     Marks the interactive controls (slider, buttons, queue list) as passthrough regions
    ///     so that the custom title-bar drag does not intercept their pointer / wheel input.
    ///     WinUI 3's automatic exclusion for controls inside a custom title bar is unreliable
    ///     for custom-styled buttons and scrollable content, so we register the regions explicitly.
    /// </summary>
    private void UpdateInteractiveRegions()
    {
        if (_isUnloaded) return;
        if (XamlRoot is null) return;

        try
        {
            var scale = XamlRoot.RasterizationScale;
            var rects = new List<RectInt32>();

            AddRectIfVisible(rects, MediaSeekerSlider, scale);
            AddRectIfVisible(rects, MediaControlsPanel, scale);
            AddRectIfVisible(rects, BackButton, scale);
            AddRectIfVisible(rects, RestoreButton, scale);
            AddRectIfVisible(rects, QueueListView, scale);
            AddRectIfVisible(rects, ArtistTextBlock, scale);

            var windowId = _parentWindow.AppWindow.Id;
            var nonClientSource = InputNonClientPointerSource.GetForWindowId(windowId);
            nonClientSource.SetRegionRects(NonClientRegionKind.Passthrough, rects.ToArray());
        }
        catch
        {
            // XamlRoot or AppWindow may be transiently unavailable during teardown; ignore.
        }
    }

    private void AddRectIfVisible(List<RectInt32> rects, FrameworkElement element, double scale)
    {
        if (element.Visibility != Visibility.Visible) return;
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return;

        var bounds = element.TransformToVisual(this).TransformBounds(
            new Rect(0, 0, element.ActualWidth, element.ActualHeight));

        rects.Add(new RectInt32(
            (int)Math.Floor(bounds.X * scale),
            (int)Math.Floor(bounds.Y * scale),
            (int)Math.Ceiling(bounds.Width * scale),
            (int)Math.Ceiling(bounds.Height * scale)));
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_isUnloaded) return;

        if (args.WindowActivationState == WindowActivationState.Deactivated)
            AnimateHoverControls(visible: false, useTransitions: false);
    }

    private void OnRestoreButtonClick(object sender, RoutedEventArgs e)
    {
        RestoreButtonClicked?.Invoke(this, EventArgs.Empty);
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_isUnloaded) return;
        AnimateHoverControls(visible: true);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_isUnloaded) return;
        AnimateHoverControls(visible: false);
    }

    private void AnimateHoverControls(bool visible, bool useTransitions = true)
    {
        var targetOpacity = visible ? 1f : 0f;

        if (useTransitions)
        {
            CompositionAnimationHelper.AnimateOpacity(HoverControlsContainer, targetOpacity, 200);
        }
        else
        {
            CompositionAnimationHelper.SetOpacityImmediate(HoverControlsContainer, targetOpacity);
        }
    }

    private void VolumeButton_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var properties = e.GetCurrentPoint(sender as UIElement).Properties;
        var delta = properties.MouseWheelDelta;

        double newVolume = ViewModel.CurrentVolume + (delta > 0 ? 5 : -5);
        ViewModel.CurrentVolume = Math.Clamp(newVolume, 0, 100);
        e.Handled = true;
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
}

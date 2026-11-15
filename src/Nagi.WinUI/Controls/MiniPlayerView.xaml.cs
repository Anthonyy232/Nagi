using Windows.Foundation;
using Windows.Graphics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Nagi.Presentation.Player;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.Controls;

public sealed partial class MiniPlayerView : UserControl
{
    private readonly Window _parentWindow;
    // The whole view drags the window, except these, which must still take clicks and wheel input.
    private readonly FrameworkElement[] _interactiveElements;
    private bool _isUnloaded;

    public MiniPlayerView(Window parentWindow, PlayerViewModel viewModel)
    {
        InitializeComponent();
        _parentWindow = parentWindow;
        ViewModel = viewModel;
        _interactiveElements = [.. Player.InteractiveElements, RestoreButton];

        HoverDetector.PointerEntered += OnPointerEntered;
        HoverDetector.PointerExited += OnPointerExited;
        HoverDetector.PointerCanceled += OnPointerExited;

        foreach (var element in _interactiveElements) element.SizeChanged += OnInteractiveRegionChanged;
        SizeChanged += OnInteractiveRegionChanged;
        Player.ViewChanged += () => DispatcherQueue.TryEnqueue(UpdateInteractiveRegions);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public PlayerViewModel ViewModel { get; }

    /// <summary>
    ///     The element the parent window should register as its title-bar drag region.
    /// </summary>
    public UIElement TitleBarDragRegion => DragHandle;

    public event EventHandler? RestoreButtonClicked;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isUnloaded = false;
        _parentWindow.Activated += OnWindowActivated;

        // Initialize hover controls visual to ensure first hover works immediately.
        CompositionAnimationHelper.SetOpacityImmediate(HoverControlsContainer, 0f);

        UpdateInteractiveRegions();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isUnloaded = true;
        _parentWindow.Activated -= OnWindowActivated;
    }

    private void OnInteractiveRegionChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateInteractiveRegions();
    }

    /// <summary>
    ///     Marks the interactive controls as passthrough regions so that the custom title-bar drag does not intercept
    ///     their pointer / wheel input. WinUI 3's automatic exclusion for controls inside a custom title bar is
    ///     unreliable for custom-styled buttons and scrollable content, so we register the regions explicitly.
    /// </summary>
    private void UpdateInteractiveRegions()
    {
        if (_isUnloaded) return;
        if (XamlRoot is null) return;

        try
        {
            var scale = XamlRoot.RasterizationScale;
            var rects = new List<RectInt32>();
            foreach (var element in _interactiveElements) AddRectIfVisible(rects, element, scale);

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

    private void RestoreButton_Click(object sender, RoutedEventArgs e)
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
}

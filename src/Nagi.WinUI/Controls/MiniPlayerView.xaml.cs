using System;
using Windows.System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.ViewModels;

namespace Nagi.WinUI.Controls;

/// <summary>
///     A user control for the mini-player, which displays track information and provides
///     media controls.
/// </summary>
public sealed partial class MiniPlayerView : UserControl
{
    private readonly Window _parentWindow;
    private bool _isUnloaded;

    /// <summary>
    ///     Initializes a new instance of the <see cref="MiniPlayerView" /> class.
    /// </summary>
    /// <param name="parentWindow">The parent window that hosts this control.</param>
    public MiniPlayerView(Window parentWindow)
    {
        InitializeComponent();
        _parentWindow = parentWindow;

        ViewModel = App.Services!.GetRequiredService<PlayerViewModel>();
        DataContext = this;

        SubscribeToEvents();
    }

    /// <summary>
    ///     Gets the view model that provides data and commands for the player UI.
    /// </summary>
    public PlayerViewModel ViewModel { get; }

    /// <summary>
    ///     The element the parent window should register as its title-bar drag region.
    /// </summary>
    public UIElement TitleBarDragRegion => DragHandle;

    /// <summary>
    ///     Occurs when the user clicks the button to restore the main application window.
    /// </summary>
    public event EventHandler? RestoreButtonClicked;

    private void SubscribeToEvents()
    {
        RestoreButton.Click += OnRestoreButtonClick;
        _parentWindow.Activated += OnWindowActivated;

        // Pointer events for hover effects.
        HoverDetector.PointerEntered += OnPointerEntered;
        HoverDetector.PointerExited += OnPointerExited;
        HoverDetector.PointerCanceled += OnPointerExited;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Initialize hover controls visual to ensure first hover works immediately.
        CompositionAnimationHelper.SetOpacityImmediate(HoverControlsContainer, 0f);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Ensure cleanup logic runs only once.
        if (_isUnloaded) return;
        _isUnloaded = true;

        // Unsubscribe from all events to prevent memory leaks.
        RestoreButton.Click -= OnRestoreButtonClick;
        _parentWindow.Activated -= OnWindowActivated;

        HoverDetector.PointerEntered -= OnPointerEntered;
        HoverDetector.PointerExited -= OnPointerExited;
        HoverDetector.PointerCanceled -= OnPointerExited;

        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_isUnloaded) return;

        // Hide hover controls when the window is no longer in the foreground.
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

    /// <summary>
    ///     Animates the hover controls visibility using GPU-accelerated Composition.
    /// </summary>
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

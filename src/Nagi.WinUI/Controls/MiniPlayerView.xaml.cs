using System;
using Windows.Graphics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Nagi.WinUI.ViewModels;

namespace Nagi.WinUI.Controls;

/// <summary>
///     A user control for the mini-player, which displays track information and provides
///     media controls. It manages its own visual states and handles window dragging.
/// </summary>
public sealed partial class MiniPlayerView : UserControl
{
    private readonly Window _parentWindow;
    private bool _isDragging;
    private bool _isUnloaded;

    // Stores the last known pointer position to calculate movement delta.
    private PointInt32 _lastPointerPosition;

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
    ///     Occurs when the user clicks the button to restore the main application window.
    /// </summary>
    public event EventHandler? RestoreButtonClicked;

    private void SubscribeToEvents()
    {
        RestoreButton.Click += OnRestoreButtonClick;
        _parentWindow.Activated += OnWindowActivated;

        // Pointer events for custom window dragging.
        DragHandle.PointerPressed += OnDragHandlePointerPressed;
        DragHandle.PointerMoved += OnDragHandlePointerMoved;
        DragHandle.PointerReleased += OnDragHandlePointerReleased;
        DragHandle.PointerCaptureLost += OnDragHandlePointerCaptureLost;

        // Pointer events for hover effects.
        HoverDetector.PointerEntered += OnPointerEntered;
        HoverDetector.PointerExited += OnPointerExited;
        HoverDetector.PointerCanceled += OnPointerExited;

        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Ensure cleanup logic runs only once.
        if (_isUnloaded) return;
        _isUnloaded = true;

        // Unsubscribe from all events to prevent memory leaks.
        RestoreButton.Click -= OnRestoreButtonClick;
        _parentWindow.Activated -= OnWindowActivated;

        DragHandle.PointerPressed -= OnDragHandlePointerPressed;
        DragHandle.PointerMoved -= OnDragHandlePointerMoved;
        DragHandle.PointerReleased -= OnDragHandlePointerReleased;
        DragHandle.PointerCaptureLost -= OnDragHandlePointerCaptureLost;

        HoverDetector.PointerEntered -= OnPointerEntered;
        HoverDetector.PointerExited -= OnPointerExited;
        HoverDetector.PointerCanceled -= OnPointerExited;

        Unloaded -= OnUnloaded;
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_isUnloaded) return;

        // Hide hover controls when the window is no longer in the foreground.
        if (args.WindowActivationState == WindowActivationState.Deactivated)
            VisualStateManager.GoToState(this, "Normal", false);
    }

    private void OnRestoreButtonClick(object sender, RoutedEventArgs e)
    {
        RestoreButtonClicked?.Invoke(this, EventArgs.Empty);
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_isUnloaded) return;
        VisualStateManager.GoToState(this, "MouseOver", true);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_isUnloaded) return;
        VisualStateManager.GoToState(this, "Normal", true);
    }

    // Initiates a window drag operation when the left mouse button is pressed on the drag handle.
    private void OnDragHandlePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isUnloaded) return;

        var pointerPoint = e.GetCurrentPoint(DragHandle);
        if (pointerPoint.Properties.IsLeftButtonPressed)
        {
            _isDragging = true;
            var position = pointerPoint.Position;
            _lastPointerPosition = new PointInt32((int)position.X, (int)position.Y);
            DragHandle.CapturePointer(e.Pointer);
            e.Handled = true;
        }
    }

    // Moves the window based on the pointer's movement while dragging.
    private void OnDragHandlePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging || _isUnloaded) return;

        var currentPosition = e.GetCurrentPoint(DragHandle).Position;
        var currentPointerPosition = new PointInt32((int)currentPosition.X, (int)currentPosition.Y);

        var deltaX = currentPointerPosition.X - _lastPointerPosition.X;
        var deltaY = currentPointerPosition.Y - _lastPointerPosition.Y;

        // Only move the window if the pointer has actually moved.
        if (deltaX != 0 || deltaY != 0)
        {
            var appWindow = _parentWindow.AppWindow;
            var currentWindowPosition = appWindow.Position;
            var newPosition = new PointInt32(
                currentWindowPosition.X + deltaX,
                currentWindowPosition.Y + deltaY
            );

            appWindow.Move(newPosition);
        }

        e.Handled = true;
    }

    // NOTE: This method intentionally does nothing in normal operation. Cleanup is handled by
    // OnDragHandlePointerCaptureLost for more reliable behavior across edge cases (fast drags,
    // focus changes, etc.). Explicitly releasing the pointer here causes visual glitches.
    private void OnDragHandlePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isUnloaded) return;

        if (_isDragging)
        {
            _isDragging = false;
            DragHandle.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    // Ensures dragging stops if pointer capture is lost unexpectedly.
    private void OnDragHandlePointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_isUnloaded) return;
        _isDragging = false;
    }
}
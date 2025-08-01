using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Nagi.WinUI.ViewModels;
using System;
using Windows.Graphics;

namespace Nagi.WinUI.Controls;

/// <summary>
/// A user control for the mini-player, which displays track information and provides basic controls.
/// It manages its own visual states and handles window dragging.
/// </summary>
public sealed partial class MiniPlayerView : UserControl {
    /// <summary>
    /// Gets the view model for the player, which provides data for the UI.
    /// </summary>
    public PlayerViewModel ViewModel { get; }

    /// <summary>
    /// Occurs when the user clicks the button to restore the main application window.
    /// </summary>
    public event EventHandler? RestoreButtonClicked;

    private readonly Window _parentWindow;
    private bool _isDragging = false;
    private PointInt32 _lastPointerPosition;
    private bool _isUnloaded = false;

    public MiniPlayerView(Window parentWindow) {
        InitializeComponent();
        _parentWindow = parentWindow;

        ViewModel = App.Services!.GetRequiredService<PlayerViewModel>();
        DataContext = this;

        SubscribeToEvents();
    }

    /// <summary>
    /// Provides the UI element designated as the draggable region for the window.
    /// </summary>
    /// <returns>The Border element that acts as the window's drag handle.</returns>
    public Border GetDragHandle() => DragHandle;

    private void SubscribeToEvents() {
        RestoreButton.Click += OnRestoreButtonClick;
        _parentWindow.Activated += OnWindowActivated;

        DragHandle.PointerPressed += OnDragHandlePointerPressed;
        DragHandle.PointerMoved += OnDragHandlePointerMoved;
        DragHandle.PointerReleased += OnDragHandlePointerReleased;
        DragHandle.PointerCaptureLost += OnDragHandlePointerCaptureLost;

        HoverDetector.PointerEntered += OnPointerEntered;
        HoverDetector.PointerExited += OnPointerExited;
        HoverDetector.PointerCanceled += OnPointerExited;

        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) {
        // Prevent duplicate unsubscriptions.
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

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args) {
        if (_isUnloaded) return;

        // When the window loses focus, reset the visual state to hide hover controls.
        if (args.WindowActivationState == WindowActivationState.Deactivated) {
            VisualStateManager.GoToState(this, "Normal", false);
        }
    }

    private void OnRestoreButtonClick(object sender, RoutedEventArgs e) {
        RestoreButtonClicked?.Invoke(this, EventArgs.Empty);
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) {
        if (_isUnloaded) return;
        VisualStateManager.GoToState(this, "MouseOver", true);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e) {
        if (_isUnloaded) return;
        VisualStateManager.GoToState(this, "Normal", true);
    }

    private void OnDragHandlePointerPressed(object sender, PointerRoutedEventArgs e) {
        if (_isUnloaded) return;

        var pointerPoint = e.GetCurrentPoint(DragHandle);
        if (pointerPoint.Properties.IsLeftButtonPressed) {
            _isDragging = true;
            var position = pointerPoint.Position;
            _lastPointerPosition = new PointInt32((int)position.X, (int)position.Y);
            DragHandle.CapturePointer(e.Pointer);
            e.Handled = true;
        }
    }

    private void OnDragHandlePointerMoved(object sender, PointerRoutedEventArgs e) {
        if (!_isDragging || _isUnloaded) return;

        var currentPosition = e.GetCurrentPoint(DragHandle).Position;
        var currentPointerPosition = new PointInt32((int)currentPosition.X, (int)currentPosition.Y);

        var deltaX = currentPointerPosition.X - _lastPointerPosition.X;
        var deltaY = currentPointerPosition.Y - _lastPointerPosition.Y;

        // Only move the window if there is a change in position.
        if (deltaX != 0 || deltaY != 0) {
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

    private void OnDragHandlePointerReleased(object sender, PointerRoutedEventArgs e) {
        if (_isUnloaded) return;

        if (_isDragging) {
            _isDragging = false;
            DragHandle.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    private void OnDragHandlePointerCaptureLost(object sender, PointerRoutedEventArgs e) {
        _isDragging = false;
    }
}
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Nagi.WinUI.ViewModels;
using System;

namespace Nagi.WinUI.Controls;

/// <summary>
/// The user control for the mini-player, displaying track information and controls.
/// It manages its visual states for hover effects and interacts with its parent window.
/// </summary>
public sealed partial class MiniPlayerView : UserControl {
    /// <summary>
    /// Gets the view model for the player, providing data for the UI.
    /// </summary>
    public PlayerViewModel ViewModel { get; }

    /// <summary>
    /// Occurs when the user clicks the button to restore the main window.
    /// </summary>
    public event EventHandler? RestoreButtonClicked;

    private readonly Window _parentWindow;

    public MiniPlayerView(Window parentWindow) {
        InitializeComponent();
        _parentWindow = parentWindow;

        ViewModel = App.Services!.GetRequiredService<PlayerViewModel>();
        DataContext = this;

        SubscribeToEvents();
    }

    /// <summary>
    /// Provides the UI element designated as the draggable region for the window title bar.
    /// </summary>
    /// <returns>The Border element that acts as a drag handle.</returns>
    public Border GetDragHandle() => DragHandle;

    private void SubscribeToEvents() {
        RestoreButton.Click += OnRestoreButtonClick;
        _parentWindow.Activated += OnWindowActivated;

        // The PointerCanceled event is used alongside PointerExited to ensure the
        // hover state is correctly reset if the pointer is captured by another element
        // or the window loses focus while the pointer is over the control.
        HoverDetector.PointerCanceled += OnPointerExited;

        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) {
        // Unsubscribe from all events to prevent memory leaks.
        RestoreButton.Click -= OnRestoreButtonClick;
        _parentWindow.Activated -= OnWindowActivated;
        HoverDetector.PointerCanceled -= OnPointerExited;
        Unloaded -= OnUnloaded;
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args) {
        // When the window is deactivated, reset the visual state to normal
        // to ensure hover controls are hidden.
        if (args.WindowActivationState == WindowActivationState.Deactivated) {
            VisualStateManager.GoToState(this, "Normal", false);
        }
    }

    private void OnRestoreButtonClick(object sender, RoutedEventArgs e) {
        RestoreButtonClicked?.Invoke(this, EventArgs.Empty);
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) {
        VisualStateManager.GoToState(this, "MouseOver", true);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e) {
        VisualStateManager.GoToState(this, "Normal", true);
    }
}
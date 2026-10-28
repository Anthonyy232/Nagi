using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Nagi.Presentation.ViewModels;
using Windows.System;

namespace Nagi.WinUI.Helpers;

/// <summary>
///     Input the player views share: the main page, the mini player and the tray popup.
/// </summary>
internal static class PlayerInputHelper
{
    /// <summary>
    ///     Tells the view model while the user drags or steps <paramref name="slider" />, so position updates don't move
    ///     the thumb under them.
    /// </summary>
    public static void AttachSeekSlider(Slider slider, PlayerViewModel viewModel)
    {
        // Slider marks its own pointer events handled, so plain += handlers would never see a drag start or end.
        slider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => viewModel.IsUserDraggingSlider = true), true);
        slider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => viewModel.IsUserDraggingSlider = false), true);
        slider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => viewModel.IsUserDraggingSlider = false), true);
        slider.KeyDown += (_, e) =>
        {
            if (IsSeekKey(e.Key)) viewModel.IsUserDraggingSlider = true;
        };
        slider.KeyUp += (_, e) =>
        {
            if (IsSeekKey(e.Key)) viewModel.IsUserDraggingSlider = false;
        };
    }

    public static void StepVolumeWithWheel(object sender, PointerRoutedEventArgs e, PlayerViewModel viewModel)
    {
        var delta = e.GetCurrentPoint((UIElement)sender).Properties.MouseWheelDelta;
        if (delta != 0) viewModel.StepVolume(delta > 0);
        e.Handled = true;
    }

    private static bool IsSeekKey(VirtualKey key) =>
        key is VirtualKey.Left or VirtualKey.Right or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Home
            or VirtualKey.End;
}

﻿using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics;
using Nagi.WinUI.Popups;

namespace Nagi.WinUI.Helpers;

/// <summary>
/// Provides robust, cancellable, and smooth animations for pop-up windows.
/// This implementation uses an async/await loop with cancellation handling to prevent
/// conflicting animations and ensure a jank-free experience.
/// </summary>
internal static class PopupAnimation {
    // Defines the duration and style of the animations.
    private const float ShowDurationMs = 250f;
    private const float HideDurationMs = 200f;
    private const float StartScale = 0.85f;
    private const int AnimationFrameDelayMs = 16; // 60 fps

    // Manages the cancellation of ongoing animations to prevent conflicts.
    private static CancellationTokenSource? _animationCts;

    /// <summary>
    /// Animates a window into view with a scale and fade effect.
    /// Cancels any previously running animation on the same window.
    /// </summary>
    public static async Task AnimateIn(TrayPopup window, RectInt32 finalRect) {
        // Cancel any existing animation and create a new cancellation token for this one.
        _animationCts?.Cancel();
        _animationCts = new CancellationTokenSource();
        var token = _animationCts.Token;

        // Calculate the starting geometry, scaled down but centered on the final position.
        int startWidth = (int)(finalRect.Width * StartScale);
        int startHeight = (int)(finalRect.Height * StartScale);
        int startX = finalRect.X + (finalRect.Width - startWidth) / 2;
        int startY = finalRect.Y + (finalRect.Height - startHeight) / 2;
        var startRect = new RectInt32(startX, startY, startWidth, startHeight);

        // Set the window to its initial, invisible state and then show it.
        window.SetWindowOpacity(0);
        window.AppWindow.MoveAndResize(startRect);
        WindowActivator.ActivatePopupWindow(window);

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < ShowDurationMs) {
            if (token.IsCancellationRequested) return;

            float progress = (float)stopwatch.Elapsed.TotalMilliseconds / ShowDurationMs;
            float easedProgress = EaseOutCubic(progress);

            // Interpolate all geometric properties from start to final using the eased progress.
            int newWidth = (int)(startRect.Width + (finalRect.Width - startRect.Width) * easedProgress);
            int newHeight = (int)(startRect.Height + (finalRect.Height - startRect.Height) * easedProgress);
            int newX = (int)(startRect.X + (finalRect.X - startRect.X) * easedProgress);
            int newY = (int)(startRect.Y + (finalRect.Y - startRect.Y) * easedProgress);
            byte newAlpha = (byte)(255 * easedProgress);

            window.AppWindow.MoveAndResize(new RectInt32(newX, newY, newWidth, newHeight));
            window.SetWindowOpacity(newAlpha);

            await Task.Delay(AnimationFrameDelayMs);
        }

        // If not cancelled, ensure the final state is set perfectly.
        if (!token.IsCancellationRequested) {
            window.AppWindow.MoveAndResize(finalRect);
            window.SetWindowOpacity(255);
        }
    }

    /// <summary>
    /// Animates a window out of view with a reverse scale and fade effect.
    /// Cancels any previously running animation on the same window.
    /// </summary>
    public static async Task AnimateOut(TrayPopup window) {
        // Cancel any existing animation and create a new cancellation token for this one.
        _animationCts?.Cancel();
        _animationCts = new CancellationTokenSource();
        var token = _animationCts.Token;

        var startRect = new RectInt32(window.AppWindow.Position.X, window.AppWindow.Position.Y, window.AppWindow.Size.Width, window.AppWindow.Size.Height);

        // The final state is scaled down and centered relative to the start position.
        int finalWidth = (int)(startRect.Width * StartScale);
        int finalHeight = (int)(startRect.Height * StartScale);
        int finalX = startRect.X + (startRect.Width - finalWidth) / 2;
        int finalY = startRect.Y + (startRect.Height - finalHeight) / 2;
        var finalRect = new RectInt32(finalX, finalY, finalWidth, finalHeight);

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < HideDurationMs) {
            if (token.IsCancellationRequested) return;

            float progress = (float)stopwatch.Elapsed.TotalMilliseconds / HideDurationMs;
            float easedProgress = EaseInCubic(progress);

            // Interpolate all geometric properties from start to final.
            int newWidth = (int)(startRect.Width + (finalRect.Width - startRect.Width) * easedProgress);
            int newHeight = (int)(startRect.Height + (finalRect.Height - startRect.Height) * easedProgress);
            int newX = (int)(startRect.X + (finalRect.X - startRect.X) * easedProgress);
            int newY = (int)(startRect.Y + (finalRect.Y - startRect.Y) * easedProgress);
            byte newAlpha = (byte)(255 * (1 - easedProgress));

            window.AppWindow.MoveAndResize(new RectInt32(newX, newY, newWidth, newHeight));
            window.SetWindowOpacity(newAlpha);

            await Task.Delay(AnimationFrameDelayMs);
        }

        // If not cancelled, hide and reset the window for the next time it's shown.
        if (!token.IsCancellationRequested) {
            window.AppWindow.Hide();
            window.SetWindowOpacity(255);
        }
    }

    /// <summary>
    /// Cubic easing function that starts fast and decelerates.
    /// </summary>
    private static float EaseOutCubic(float progress) => 1 - MathF.Pow(1 - progress, 3);

    /// <summary>
    /// Cubic easing function that starts slow and accelerates.
    /// </summary>
    private static float EaseInCubic(float progress) => progress * progress * progress;
}
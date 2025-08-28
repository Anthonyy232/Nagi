﻿using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nagi.WinUI.Popups;

namespace Nagi.WinUI.Helpers;

/// <summary>
///     Provides robust, cancellable, and smooth animations for pop-up windows.
///     This implementation uses an async/await loop with cancellation handling to prevent
///     conflicting animations and ensure a jank-free experience.
/// </summary>
internal static class PopupAnimation
{
    // Defines the duration and style of the animations.
    private const float ShowDurationMs = 250f;
    private const float HideDurationMs = 200f;
    private const float StartScale = 0.85f;
    private const int AnimationFrameDelayMs = 16; // 60 fps

    // Manages the cancellation of ongoing animations to prevent conflicts.
    private static CancellationTokenSource? _animationCts;

    private static ILogger? _logger;

    private static ILogger Logger =>
        _logger ??= App.Services!.GetRequiredService<ILoggerFactory>().CreateLogger("PopupAnimation");

    /// <summary>
    ///     Animates a window into view with a scale and fade effect.
    ///     Cancels any previously running animation on the same window.
    /// </summary>
    public static async Task AnimateIn(TrayPopup window, RectInt32 finalRect)
    {
        // Defensively check if the window is valid before starting.
        if (window?.AppWindow is null) return;

        try
        {
            // Cancel any existing animation and create a new cancellation token for this one.
            if (_animationCts is not null)
            {
                Logger.LogDebug("Cancelling previous animation before starting AnimateIn.");
                _animationCts.Cancel();
            }

            _animationCts = new CancellationTokenSource();
            var token = _animationCts.Token;

            // Calculate the starting geometry, scaled down but centered on the final position.
            var startWidth = (int)(finalRect.Width * StartScale);
            var startHeight = (int)(finalRect.Height * StartScale);
            var startX = finalRect.X + (finalRect.Width - startWidth) / 2;
            var startY = finalRect.Y + (finalRect.Height - startHeight) / 2;
            var startRect = new RectInt32(startX, startY, startWidth, startHeight);

            // Set the window to its initial, invisible state and then show it.
            window.SetWindowOpacity(0);
            window.AppWindow.MoveAndResize(startRect);
            WindowActivator.ActivatePopupWindow(window);

            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < ShowDurationMs)
            {
                if (token.IsCancellationRequested)
                {
                    Logger.LogDebug("AnimateIn was cancelled.");
                    return;
                }

                var progress = (float)stopwatch.Elapsed.TotalMilliseconds / ShowDurationMs;
                var easedProgress = EaseOutCubic(progress);

                // Interpolate all geometric properties from start to final using the eased progress.
                var newWidth = (int)(startRect.Width + (finalRect.Width - startRect.Width) * easedProgress);
                var newHeight = (int)(startRect.Height + (finalRect.Height - startRect.Height) * easedProgress);
                var newX = (int)(startRect.X + (finalRect.X - startRect.X) * easedProgress);
                var newY = (int)(startRect.Y + (finalRect.Y - startRect.Y) * easedProgress);
                var newAlpha = (byte)(255 * easedProgress);

                window.AppWindow.MoveAndResize(new RectInt32(newX, newY, newWidth, newHeight));
                window.SetWindowOpacity(newAlpha);

                await Task.Delay(AnimationFrameDelayMs);
            }

            // If not cancelled, ensure the final state is set perfectly.
            if (!token.IsCancellationRequested)
            {
                window.AppWindow.MoveAndResize(finalRect);
                window.SetWindowOpacity(255);
            }
        }
        catch (Exception ex)
        {
            // This can happen if the window is closed during the animation.
            Logger.LogWarning(ex,
                "AnimateIn failed gracefully. This can happen if the window is closed during animation.");
        }
    }

    /// <summary>
    ///     Animates a window out of view with a reverse scale and fade effect.
    ///     Cancels any previously running animation on the same window.
    /// </summary>
    public static async Task AnimateOut(TrayPopup window)
    {
        // Defensively check if the window is valid and visible before starting.
        if (window?.AppWindow is null || !window.AppWindow.IsVisible) return;

        try
        {
            // Cancel any existing animation and create a new cancellation token for this one.
            if (_animationCts is not null)
            {
                Logger.LogDebug("Cancelling previous animation before starting AnimateOut.");
                _animationCts.Cancel();
            }

            _animationCts = new CancellationTokenSource();
            var token = _animationCts.Token;

            var startRect = new RectInt32(window.AppWindow.Position.X, window.AppWindow.Position.Y,
                window.AppWindow.Size.Width, window.AppWindow.Size.Height);

            // The final state is scaled down and centered relative to the start position.
            var finalWidth = (int)(startRect.Width * StartScale);
            var finalHeight = (int)(startRect.Height * StartScale);
            var finalX = startRect.X + (startRect.Width - finalWidth) / 2;
            var finalY = startRect.Y + (startRect.Height - finalHeight) / 2;
            var finalRect = new RectInt32(finalX, finalY, finalWidth, finalHeight);

            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < HideDurationMs)
            {
                if (token.IsCancellationRequested)
                {
                    Logger.LogDebug("AnimateOut was cancelled.");
                    return;
                }

                var progress = (float)stopwatch.Elapsed.TotalMilliseconds / HideDurationMs;
                var easedProgress = EaseInCubic(progress);

                // Interpolate all geometric properties from start to final.
                var newWidth = (int)(startRect.Width + (finalRect.Width - startRect.Width) * easedProgress);
                var newHeight = (int)(startRect.Height + (finalRect.Height - startRect.Height) * easedProgress);
                var newX = (int)(startRect.X + (finalRect.X - startRect.X) * easedProgress);
                var newY = (int)(startRect.Y + (finalRect.Y - startRect.Y) * easedProgress);
                var newAlpha = (byte)(255 * (1 - easedProgress));

                window.AppWindow.MoveAndResize(new RectInt32(newX, newY, newWidth, newHeight));
                window.SetWindowOpacity(newAlpha);

                await Task.Delay(AnimationFrameDelayMs);
            }

            // If not cancelled, hide and reset the window for the next time it's shown.
            if (!token.IsCancellationRequested)
            {
                window.AppWindow.Hide();
                window.SetWindowOpacity(255);
            }
        }
        catch (Exception ex)
        {
            // This can happen if the window is closed during the animation.
            Logger.LogWarning(ex,
                "AnimateOut failed gracefully. This can happen if the window is closed during animation.");
            // Ensure the window is hidden if the animation fails mid-way.
            if (window?.AppWindow is not null) window.AppWindow.Hide();
        }
    }

    /// <summary>
    ///     Cubic easing function that starts fast and decelerates.
    /// </summary>
    private static float EaseOutCubic(float progress)
    {
        return 1 - MathF.Pow(1 - progress, 3);
    }

    /// <summary>
    ///     Cubic easing function that starts slow and accelerates.
    /// </summary>
    private static float EaseInCubic(float progress)
    {
        return progress * progress * progress;
    }
}
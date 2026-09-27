using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media;
using Nagi.WinUI.Popups;

namespace Nagi.WinUI.Helpers;

/// <summary>
///     Animates tray popup bounds and opacity, cancelling any previous transition.
/// </summary>
internal static class PopupAnimation
{
    private const float ShowDurationMs = 250f;
    private const float HideDurationMs = 200f;
    private const float StartScale = 0.85f;

    private static CancellationTokenSource? _animationCts;

    private static ILogger? _logger;

    private static ILogger Logger =>
        _logger ??= App.Services!.GetRequiredService<ILoggerFactory>().CreateLogger("PopupAnimation");

    /// <summary>
    ///     Cancels all ongoing animations. Call this during app shutdown to prevent TaskCanceledException.
    /// </summary>
    public static void CancelAllAnimations()
    {
        _animationCts?.Cancel();
        _animationCts?.Dispose();
        _animationCts = null;
    }

    /// <summary>
    ///     Animates a window into view with a scale and fade effect.
    ///     Cancels any previously running animation on the same window.
    /// </summary>
    public static async Task AnimateIn(TrayPopup window, RectInt32 finalRect)
    {
        if (window?.AppWindow is null) return;

        try
        {
            CancelAllAnimations();
            _animationCts = new CancellationTokenSource();
            var token = _animationCts.Token;

            var startWidth = (int)(finalRect.Width * StartScale);
            var startHeight = (int)(finalRect.Height * StartScale);

            var startX = finalRect.X + (finalRect.Width - startWidth) / 2;
            var startY = finalRect.Y + (finalRect.Height - startHeight) / 2;

            var startRect = new RectInt32(startX, startY, startWidth, startHeight);

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

                var progress = Math.Clamp((float)stopwatch.Elapsed.TotalMilliseconds / ShowDurationMs, 0f, 1f);
                var easedProgress = EaseOutCubic(progress);

                var newWidth = (int)float.Lerp(startRect.Width, finalRect.Width, easedProgress);
                var newHeight = (int)float.Lerp(startRect.Height, finalRect.Height, easedProgress);
                var newX = (int)float.Lerp(startRect.X, finalRect.X, easedProgress);
                var newY = (int)float.Lerp(startRect.Y, finalRect.Y, easedProgress);
                var newAlpha = (byte)(255 * easedProgress);

                window.AppWindow.MoveAndResize(new RectInt32(newX, newY, newWidth, newHeight));
                window.SetWindowOpacity(newAlpha);

                await YieldToRendering(token);
            }

            if (!token.IsCancellationRequested)
            {
                window.AppWindow.MoveAndResize(finalRect);
                window.SetWindowOpacity(255);
            }
        }
        catch (OperationCanceledException)
        {
            Logger.LogDebug("AnimateIn cancelled during shutdown.");
        }
        catch (Exception ex)
        {
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
        if (window?.AppWindow is null || !window.AppWindow.IsVisible) return;

        try
        {
            CancelAllAnimations();
            _animationCts = new CancellationTokenSource();
            var token = _animationCts.Token;

            var startRect = new RectInt32(window.AppWindow.Position.X, window.AppWindow.Position.Y,
                window.AppWindow.Size.Width, window.AppWindow.Size.Height);

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

                var progress = Math.Clamp((float)stopwatch.Elapsed.TotalMilliseconds / HideDurationMs, 0f, 1f);
                var easedProgress = EaseInCubic(progress);

                var newWidth = (int)float.Lerp(startRect.Width, finalRect.Width, easedProgress);
                var newHeight = (int)float.Lerp(startRect.Height, finalRect.Height, easedProgress);
                var newX = (int)float.Lerp(startRect.X, finalRect.X, easedProgress);
                var newY = (int)float.Lerp(startRect.Y, finalRect.Y, easedProgress);
                var newAlpha = (byte)(255 * (1 - easedProgress));

                window.AppWindow.MoveAndResize(new RectInt32(newX, newY, newWidth, newHeight));
                window.SetWindowOpacity(newAlpha);

                await YieldToRendering(token);
            }

            if (!token.IsCancellationRequested)
            {
                window.AppWindow.Hide();
                window.SetWindowOpacity(255);
            }
        }
        catch (OperationCanceledException)
        {
            Logger.LogDebug("AnimateOut cancelled during shutdown.");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "AnimateOut failed gracefully. This can happen if the window is closed during animation.");
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

    private static async Task YieldToRendering(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRendering(object? sender, object args) => tcs.TrySetResult();

        CompositionTarget.Rendering += OnRendering;
        try
        {
            // Keep cleanup on the UI thread even if cancellation comes from another thread.
            await tcs.Task.WaitAsync(token);
        }
        finally
        {
            CompositionTarget.Rendering -= OnRendering;
        }
    }
}

using System.Diagnostics;
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
    private const float StartScale = 0.85f;
    private static CancellationTokenSource? _animationCts;
    private static ILogger? _logger;
    private static ILogger Logger =>
        _logger ??= App.Services!.GetRequiredService<ILoggerFactory>().CreateLogger("PopupAnimation");

    public static void CancelAllAnimations()
    {
        _animationCts?.Cancel();
        _animationCts?.Dispose();
        _animationCts = null;
    }

    public static Task AnimateIn(TrayPopup window, RectInt32 finalRect) =>
        AnimateAsync(window, finalRect);

    public static Task AnimateOut(TrayPopup window) => AnimateAsync(window, null);

    private static async Task AnimateAsync(TrayPopup window, RectInt32? showRect)
    {
        if (window.AppWindow is null || (showRect is null && !window.AppWindow.IsVisible)) return;
        var showing = showRect.HasValue;
        try
        {
            CancelAllAnimations();
            _animationCts = new CancellationTokenSource();
            var token = _animationCts.Token;
            var fullRect = showRect ?? new RectInt32(window.AppWindow.Position.X, window.AppWindow.Position.Y,
                window.AppWindow.Size.Width, window.AppWindow.Size.Height);
            if (!CompositionAnimationHelper.AreAnimationsEnabled)
            {
                window.SetWindowOpacity(255);
                if (showing)
                {
                    window.AppWindow.MoveAndResize(fullRect);
                    WindowActivator.ActivatePopupWindow(window);
                }
                else window.AppWindow.Hide();
                return;
            }
            var scaledWidth = (int)(fullRect.Width * StartScale);
            var scaledHeight = (int)(fullRect.Height * StartScale);
            var scaledRect = new RectInt32(fullRect.X + (fullRect.Width - scaledWidth) / 2,
                fullRect.Y + (fullRect.Height - scaledHeight) / 2, scaledWidth, scaledHeight);
            var startRect = showing ? scaledRect : fullRect;
            var endRect = showing ? fullRect : scaledRect;
            var durationMs = showing ? 250f : 200f;

            if (showing)
            {
                window.SetWindowOpacity(0);
                window.AppWindow.MoveAndResize(startRect);
                WindowActivator.ActivatePopupWindow(window);
            }

            var stopwatch = Stopwatch.StartNew();
            while (CompositionAnimationHelper.AreAnimationsEnabled && stopwatch.Elapsed.TotalMilliseconds < durationMs)
            {
                token.ThrowIfCancellationRequested();
                var progress = Math.Clamp((float)stopwatch.Elapsed.TotalMilliseconds / durationMs, 0f, 1f);
                var eased = showing ? 1 - MathF.Pow(1 - progress, 3) : progress * progress * progress;
                window.AppWindow.MoveAndResize(new RectInt32(
                    (int)float.Lerp(startRect.X, endRect.X, eased),
                    (int)float.Lerp(startRect.Y, endRect.Y, eased),
                    (int)float.Lerp(startRect.Width, endRect.Width, eased),
                    (int)float.Lerp(startRect.Height, endRect.Height, eased)));
                window.SetWindowOpacity((byte)(255 * (showing ? eased : 1 - eased)));
                await YieldToRendering(token);
            }

            token.ThrowIfCancellationRequested();
            if (showing) window.AppWindow.MoveAndResize(endRect);
            else window.AppWindow.Hide();
            window.SetWindowOpacity(255);
        }
        catch (OperationCanceledException)
        {
            Logger.LogDebug("Tray popup animation cancelled.");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Tray popup animation failed; the window may have closed.");
            if (!showing && window.AppWindow is not null) window.AppWindow.Hide();
        }
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

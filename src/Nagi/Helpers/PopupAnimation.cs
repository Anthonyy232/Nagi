using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using System;
using System.Numerics;

namespace Nagi.Helpers;

/// <summary>
/// Provides high-performance, composition-based animations for popup windows.
/// </summary>
internal static class PopupAnimation {
    private const float ShowDurationMs = 300f;
    private const float HideDurationMs = 150f;
    private static readonly Vector3 StartOffset = new(0, 20, 0);

    /// <summary>
    /// Animates a window into view after it has been shown and activated.
    /// </summary>
    /// <param name="window">The XAML Window to animate.</param>
    /// <param name="onCompleted">An optional action to run when the animation finishes.</param>
    public static void AnimateIn(Window window, Action? onCompleted = null) {
        if (window.Content is not UIElement content) return;

        var appWindow = window.AppWindow;
        var compositor = window.Compositor;
        var rootVisual = ElementCompositionPreview.GetElementVisual(content);

        // Prepare the visual for its entry animation.
        rootVisual.CenterPoint = new Vector3(appWindow.Size.Width / 2.0f, appWindow.Size.Height / 2.0f, 0);
        rootVisual.Opacity = 0.0f;
        rootVisual.Scale = new Vector3(0.7f, 0.7f, 1.0f);
        rootVisual.Offset = StartOffset; // Set initial offset before animating

        // This easing function creates a "back" effect, where the animation overshoots and then settles.
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.34f, 1.56f), new Vector2(0.64f, 1f));

        // Opacity animation: Fades from 0 to 1.
        var opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
        opacityAnimation.InsertKeyFrame(1.0f, 1.0f);
        opacityAnimation.Duration = TimeSpan.FromMilliseconds(ShowDurationMs * 0.6f);
        opacityAnimation.Target = nameof(Visual.Opacity);

        // Scale animation: Grows from 0.7 to 1.0 with an overshoot.
        var scaleAnimation = compositor.CreateVector3KeyFrameAnimation();
        scaleAnimation.InsertKeyFrame(1.0f, Vector3.One, easing);
        scaleAnimation.Duration = TimeSpan.FromMilliseconds(ShowDurationMs);
        scaleAnimation.Target = nameof(Visual.Scale);

        // Offset animation: Moves from its current offset to its final position (Vector3.Zero).
        var offsetAnimation = compositor.CreateVector3KeyFrameAnimation();
        offsetAnimation.InsertKeyFrame(1.0f, Vector3.Zero, easing);
        offsetAnimation.Duration = TimeSpan.FromMilliseconds(ShowDurationMs);
        offsetAnimation.Target = nameof(Visual.Offset);

        var animationGroup = compositor.CreateAnimationGroup();
        animationGroup.Add(opacityAnimation);
        animationGroup.Add(scaleAnimation);
        animationGroup.Add(offsetAnimation);

        // Use a scoped batch to run an action upon completion.
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        rootVisual.StartAnimationGroup(animationGroup);
        batch.End();

        batch.Completed += (s, a) => {
            // Reset the offset property after animation to avoid layout issues.
            rootVisual.Offset = Vector3.Zero;
            onCompleted?.Invoke();
        };
    }

    /// <summary>
    /// Animates a window out of view with a quick fade-and-fall effect.
    /// </summary>
    /// <param name="window">The XAML Window to animate.</param>
    /// <param name="onCompleted">An optional action to run when the window is hidden.</param>
    public static void Hide(Window window, Action? onCompleted = null) {
        var appWindow = window.AppWindow;
        if (window.Content is not UIElement content) {
            // If there's no content to animate, just hide the window immediately.
            appWindow.Hide();
            onCompleted?.Invoke();
            return;
        }

        var compositor = window.Compositor;
        var rootVisual = ElementCompositionPreview.GetElementVisual(content);

        // Use a sharp "ease-in" curve for a quick exit.
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.5f, 0.0f), new Vector2(0.9f, 0.5f));

        rootVisual.CenterPoint = new Vector3(appWindow.Size.Width / 2.0f, appWindow.Size.Height / 2.0f, 0);

        // Opacity animation: Fades from 1 to 0.
        var opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
        opacityAnimation.InsertKeyFrame(1.0f, 0.0f, easing);
        opacityAnimation.Duration = TimeSpan.FromMilliseconds(HideDurationMs);
        opacityAnimation.Target = nameof(Visual.Opacity);

        // Scale animation: Shrinks to match the show animation's start scale.
        var scaleAnimation = compositor.CreateVector3KeyFrameAnimation();
        scaleAnimation.InsertKeyFrame(1.0f, new Vector3(0.7f, 0.7f, 1.0f), easing);
        scaleAnimation.Duration = TimeSpan.FromMilliseconds(HideDurationMs);
        scaleAnimation.Target = nameof(Visual.Scale);

        // Offset animation: Moves down to the starting offset position.
        var offsetAnimation = compositor.CreateVector3KeyFrameAnimation();
        offsetAnimation.InsertKeyFrame(1.0f, StartOffset, easing);
        offsetAnimation.Duration = TimeSpan.FromMilliseconds(HideDurationMs);
        offsetAnimation.Target = nameof(Visual.Offset);

        var animationGroup = compositor.CreateAnimationGroup();
        animationGroup.Add(opacityAnimation);
        animationGroup.Add(scaleAnimation);
        animationGroup.Add(offsetAnimation);

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        rootVisual.StartAnimationGroup(animationGroup);
        batch.End();

        batch.Completed += (s, a) => {
            appWindow.Hide();

            // Reset visual properties to their default state after hiding.
            // This ensures the window appears correctly if shown again without re-creation.
            rootVisual.Opacity = 1.0f;
            rootVisual.Scale = Vector3.One;
            rootVisual.Offset = Vector3.Zero;

            onCompleted?.Invoke();
        };
    }
}
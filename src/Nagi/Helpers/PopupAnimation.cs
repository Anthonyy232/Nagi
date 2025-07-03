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
    /// Animates a window into view using a slide, scale, and fade-in effect.
    /// </summary>
    /// <param name="window">The XAML Window to animate.</param>
    /// <param name="onCompleted">An optional action to run when the animation finishes.</param>
    public static void AnimateIn(Window window, Action? onCompleted = null) {
        if (window.Content is not UIElement content) return;

        Compositor compositor = window.Compositor;
        Visual rootVisual = ElementCompositionPreview.GetElementVisual(content);

        //
        // Set the initial state of the visual for the entrance animation.
        //
        rootVisual.CenterPoint = new Vector3(window.AppWindow.Size.Width / 2.0f, window.AppWindow.Size.Height / 2.0f, 0);
        rootVisual.Opacity = 0.0f;
        rootVisual.Scale = new Vector3(0.7f, 0.7f, 1.0f);
        rootVisual.Offset = StartOffset;

        //
        // This easing function creates a "back" effect, where the animation overshoots and then settles.
        //
        CubicBezierEasingFunction easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.34f, 1.56f), new Vector2(0.64f, 1f));

        //
        // Define animations for opacity, scale, and offset.
        //
        ScalarKeyFrameAnimation opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
        opacityAnimation.InsertKeyFrame(1.0f, 1.0f);
        opacityAnimation.Duration = TimeSpan.FromMilliseconds(ShowDurationMs * 0.6f);
        opacityAnimation.Target = nameof(Visual.Opacity);

        Vector3KeyFrameAnimation scaleAnimation = compositor.CreateVector3KeyFrameAnimation();
        scaleAnimation.InsertKeyFrame(1.0f, Vector3.One, easing);
        scaleAnimation.Duration = TimeSpan.FromMilliseconds(ShowDurationMs);
        scaleAnimation.Target = nameof(Visual.Scale);

        Vector3KeyFrameAnimation offsetAnimation = compositor.CreateVector3KeyFrameAnimation();
        offsetAnimation.InsertKeyFrame(1.0f, Vector3.Zero, easing);
        offsetAnimation.Duration = TimeSpan.FromMilliseconds(ShowDurationMs);
        offsetAnimation.Target = nameof(Visual.Offset);

        //
        // Group and start the animations.
        //
        CompositionAnimationGroup animationGroup = compositor.CreateAnimationGroup();
        animationGroup.Add(opacityAnimation);
        animationGroup.Add(scaleAnimation);
        animationGroup.Add(offsetAnimation);

        CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        rootVisual.StartAnimationGroup(animationGroup);
        batch.End();

        batch.Completed += (s, a) => {
            //
            // Reset the offset property after animation to avoid layout issues.
            //
            rootVisual.Offset = Vector3.Zero;
            onCompleted?.Invoke();
        };
    }

    /// <summary>
    /// Animates a window out of view using a slide, scale, and fade-out effect.
    /// </summary>
    /// <param name="window">The XAML Window to animate.</param>
    /// <param name="onCompleted">An optional action to run after the window is hidden.</param>
    public static void Hide(Window window, Action? onCompleted = null) {
        if (window.Content is not UIElement content) {
            window.AppWindow.Hide();
            onCompleted?.Invoke();
            return;
        }

        Compositor compositor = window.Compositor;
        Visual rootVisual = ElementCompositionPreview.GetElementVisual(content);

        //
        // Use a sharp "ease-in" curve for a quick exit.
        //
        CubicBezierEasingFunction easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.5f, 0.0f), new Vector2(0.9f, 0.5f));
        rootVisual.CenterPoint = new Vector3(window.AppWindow.Size.Width / 2.0f, window.AppWindow.Size.Height / 2.0f, 0);

        //
        // Define animations for opacity, scale, and offset.
        //
        ScalarKeyFrameAnimation opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
        opacityAnimation.InsertKeyFrame(1.0f, 0.0f, easing);
        opacityAnimation.Duration = TimeSpan.FromMilliseconds(HideDurationMs);
        opacityAnimation.Target = nameof(Visual.Opacity);

        Vector3KeyFrameAnimation scaleAnimation = compositor.CreateVector3KeyFrameAnimation();
        scaleAnimation.InsertKeyFrame(1.0f, new Vector3(0.7f, 0.7f, 1.0f), easing);
        scaleAnimation.Duration = TimeSpan.FromMilliseconds(HideDurationMs);
        scaleAnimation.Target = nameof(Visual.Scale);

        Vector3KeyFrameAnimation offsetAnimation = compositor.CreateVector3KeyFrameAnimation();
        offsetAnimation.InsertKeyFrame(1.0f, StartOffset, easing);
        offsetAnimation.Duration = TimeSpan.FromMilliseconds(HideDurationMs);
        offsetAnimation.Target = nameof(Visual.Offset);

        CompositionAnimationGroup animationGroup = compositor.CreateAnimationGroup();
        animationGroup.Add(opacityAnimation);
        animationGroup.Add(scaleAnimation);
        animationGroup.Add(offsetAnimation);

        CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        rootVisual.StartAnimationGroup(animationGroup);
        batch.End();

        batch.Completed += (s, a) => {
            window.AppWindow.Hide();

            //
            // Reset visual properties to their default state after hiding.
            // This ensures the window appears correctly if shown again.
            //
            rootVisual.Opacity = 1.0f;
            rootVisual.Scale = Vector3.One;
            rootVisual.Offset = Vector3.Zero;

            onCompleted?.Invoke();
        };
    }
}
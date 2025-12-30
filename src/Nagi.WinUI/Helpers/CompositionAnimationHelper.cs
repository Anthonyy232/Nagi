using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using System;
using System.Numerics;

namespace Nagi.WinUI.Helpers;

/// <summary>
/// Provides helper methods for applying high-performance Composition-based animations.
/// These animations run on the compositor thread, ensuring 60 FPS even when the UI thread is busy.
/// </summary>
public static class CompositionAnimationHelper
{
    /// <summary>
    /// Creates a pulse animation that can be triggered programmatically.
    /// Useful for play/pause button feedback.
    /// </summary>
    /// <param name="element">The UI element to animate.</param>
    /// <param name="pulseScale">Maximum scale during the pulse (default 1.15).</param>
    public static void TriggerPulse(UIElement element, float pulseScale = 1.15f)
    {
        if (element == null) return;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;

        // Set center point for scaling from the center
        if (element.ActualSize.X == 0 || element.ActualSize.Y == 0)
        {
            // Center point will be updated on next layout if needed, 
            // but for a one-off pulse we can try fallback to a reasonable center
            visual.CenterPoint = new Vector3((float)element.RenderSize.Width / 2, (float)element.RenderSize.Height / 2, 0);
        }
        else
        {
            visual.CenterPoint = new Vector3((float)(element.ActualSize.X / 2), (float)(element.ActualSize.Y / 2), 0);
        }

        // Create a keyframe animation: 1.0 -> pulse -> 1.0
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(0.0f, new Vector3(1.0f, 1.0f, 1.0f));
        animation.InsertKeyFrame(0.4f, new Vector3(pulseScale, pulseScale, 1.0f));
        animation.InsertKeyFrame(1.0f, new Vector3(1.0f, 1.0f, 1.0f));
        animation.Duration = TimeSpan.FromMilliseconds(300);

        visual.StartAnimation("Scale", animation);
    }

    /// <summary>
    ///     Animates an element's opacity using GPU-accelerated Composition animations.
    /// </summary>
    public static void AnimateOpacity(UIElement element, float to, int durationMs, int delayMs = 0)
    {
        if (element == null) return;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;

        visual.StopAnimation("Opacity");

        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1.0f, to, compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.25f, 0.1f),
            new Vector2(0.25f, 1.0f)));
        animation.Duration = TimeSpan.FromMilliseconds(durationMs);
        animation.DelayTime = TimeSpan.FromMilliseconds(delayMs);

        visual.StartAnimation("Opacity", animation);
    }

    /// <summary>
    ///     Sets an element's opacity immediately without animation.
    /// </summary>
    public static void SetOpacityImmediate(UIElement element, float opacity)
    {
        if (element == null) return;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.Opacity = opacity;
    }

    /// <summary>
    ///     Animates an element's translation using GPU-accelerated Composition animations.
    /// </summary>
    public static void AnimateTranslation(UIElement element, Vector3 to, int durationMs, bool easeIn = false)
    {
        if (element == null) return;
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;

        visual.StopAnimation("Translation");

        var animation = compositor.CreateVector3KeyFrameAnimation();
        var easing = easeIn
            ? compositor.CreateCubicBezierEasingFunction(new Vector2(0.42f, 0f), new Vector2(1f, 1f))
            : compositor.CreateCubicBezierEasingFunction(new Vector2(0f, 0f), new Vector2(0.58f, 1f));
        animation.InsertKeyFrame(1.0f, to, easing);
        animation.Duration = TimeSpan.FromMilliseconds(durationMs);

        visual.StartAnimation("Translation", animation);
    }

    /// <summary>
    ///     Sets an element's translation immediately without animation.
    /// </summary>
    public static void SetTranslationImmediate(UIElement element, Vector3 translation)
    {
        if (element == null) return;
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.Properties.InsertVector3("Translation", translation);
    }
}


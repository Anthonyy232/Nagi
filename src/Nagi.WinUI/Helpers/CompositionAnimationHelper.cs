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


using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using System;
using System.Numerics;

namespace Nagi.WinUI.Helpers;

/// <summary>
/// Runs element animations on the compositor thread.
/// </summary>
public static class CompositionAnimationHelper
{

    public static void AnimateOpacity(UIElement element, float to, int durationMs, int delayMs = 0)
    {
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

    public static void SetOpacityImmediate(UIElement element, float opacity)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Opacity");
        visual.Opacity = opacity;
    }

    public static void AnimateScale(UIElement element, float to, int durationMs)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;

        // Center scale transitions on laid-out elements.
        var width = (float)element.ActualSize.X;
        var height = (float)element.ActualSize.Y;
        if (width > 0 && height > 0)
            visual.CenterPoint = new Vector3(width / 2f, height / 2f, 0f);

        visual.StopAnimation("Scale");

        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(1.0f, new Vector3(to, to, 1.0f), compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.25f, 0.1f),
            new Vector2(0.25f, 1.0f)));
        animation.Duration = TimeSpan.FromMilliseconds(durationMs);

        visual.StartAnimation("Scale", animation);
    }

}

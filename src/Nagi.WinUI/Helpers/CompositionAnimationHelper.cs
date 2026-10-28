using Microsoft.UI.Xaml;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;
using Windows.UI.ViewManagement;

namespace Nagi.WinUI.Helpers;

/// <summary>
/// Runs element animations on the compositor thread.
/// </summary>
public static class CompositionAnimationHelper
{
    private static UISettings SystemSettings { get; } = new();
    public static bool AreAnimationsEnabled => SystemSettings.AnimationsEnabled;

    internal static event Windows.Foundation.TypedEventHandler<UISettings, UISettingsAnimationsEnabledChangedEventArgs> AnimationsEnabledChanged
    {
        add
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                SystemSettings.AnimationsEnabledChanged += value;
        }
        remove
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                SystemSettings.AnimationsEnabledChanged -= value;
        }
    }

    public static void AnimateOpacity(UIElement element, float to, int durationMs, int delayMs = 0)
    {
        if (!AreAnimationsEnabled)
        {
            SetOpacityImmediate(element, to);
            return;
        }
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

    /// <summary>
    ///     Stops any slide on an element and puts it back fully visible in its laid-out position.
    /// </summary>
    public static void ResetSlide(UIElement element)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Translation");
        visual.StopAnimation("Opacity");
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
        visual.Opacity = 1f;
    }

    /// <summary>
    ///     Fades an element out while sliding it by <paramref name="offset" />, then calls
    ///     <paramref name="completed" /> on the UI thread. Without animations it completes immediately.
    /// </summary>
    public static void AnimateSlideOut(UIElement element, Vector3 offset, int durationMs, Action completed)
    {
        if (!AreAnimationsEnabled)
        {
            completed();
            return;
        }

        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f));
        var duration = TimeSpan.FromMilliseconds(durationMs);

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        var translation = compositor.CreateVector3KeyFrameAnimation();
        translation.InsertKeyFrame(1f, offset, easing);
        translation.Duration = duration;
        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(1f, 0f, easing);
        opacity.Duration = duration;
        visual.StartAnimation("Translation", translation);
        visual.StartAnimation("Opacity", opacity);
        batch.End();
        batch.Completed += (_, _) => completed();
    }

    /// <summary>
    ///     Fades an element in while sliding it from <paramref name="offset" /> to its laid-out position,
    ///     keeping it hidden at the offset until <paramref name="delayMs" /> has passed.
    /// </summary>
    public static void AnimateSlideIn(UIElement element, Vector3 offset, int durationMs, int delayMs = 0)
    {
        if (!AreAnimationsEnabled)
        {
            ResetSlide(element);
            return;
        }

        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Translation");
        visual.StopAnimation("Opacity");

        // Set the starting state directly: an element shown before keeps its last values through the delay
        // otherwise, so it would flash in place and then restart its entrance.
        visual.Properties.InsertVector3("Translation", offset);
        visual.Opacity = 0f;

        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1.0f));
        var duration = TimeSpan.FromMilliseconds(durationMs);
        var delay = TimeSpan.FromMilliseconds(delayMs);

        var translation = compositor.CreateVector3KeyFrameAnimation();
        translation.InsertKeyFrame(0f, offset);
        translation.InsertKeyFrame(1f, Vector3.Zero, easing);
        translation.Duration = duration;
        translation.DelayTime = delay;

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0f, 0f);
        opacity.InsertKeyFrame(1f, 1f, easing);
        opacity.Duration = duration;
        opacity.DelayTime = delay;

        visual.StartAnimation("Translation", translation);
        visual.StartAnimation("Opacity", opacity);
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
        if (!AreAnimationsEnabled)
        {
            visual.Scale = new Vector3(to, to, 1f);
            return;
        }

        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(1.0f, new Vector3(to, to, 1.0f), compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.25f, 0.1f),
            new Vector2(0.25f, 1.0f)));
        animation.Duration = TimeSpan.FromMilliseconds(durationMs);

        visual.StartAnimation("Scale", animation);
    }

}

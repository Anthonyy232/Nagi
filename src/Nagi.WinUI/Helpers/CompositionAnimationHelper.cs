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
}


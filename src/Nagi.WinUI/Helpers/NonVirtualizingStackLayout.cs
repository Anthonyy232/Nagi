using System;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Nagi.WinUI.Helpers;

/// <summary>
///     A non-virtualizing vertical stack layout for ItemsRepeater.
///     Unlike <see cref="StackLayout" />, this layout always realizes ALL elements
///     regardless of the viewport, ensuring <see cref="ItemsRepeater.TryGetElement" />
///     never returns null for valid indices.
/// </summary>
/// <remarks>
///     This is appropriate for lyrics display where item count is small (~100 max)
///     and each item is a lightweight TextBlock grid.
///     Virtualization would cause scroll-to failures and missed visual updates.
/// </remarks>
public sealed class NonVirtualizingStackLayout : NonVirtualizingLayout
{
    protected override Size MeasureOverride(NonVirtualizingLayoutContext context, Size availableSize)
    {
        var totalHeight = 0.0;
        var maxWidth = 0.0;

        foreach (var child in context.Children)
        {
            child.Measure(availableSize);
            // Round each child's height to whole pixels to prevent sub-pixel accumulation
            totalHeight += Math.Round(child.DesiredSize.Height);
            if (child.DesiredSize.Width > maxWidth)
                maxWidth = child.DesiredSize.Width;
        }

        return new Size(
            double.IsInfinity(availableSize.Width) ? maxWidth : availableSize.Width,
            totalHeight);
    }

    protected override Size ArrangeOverride(NonVirtualizingLayoutContext context, Size finalSize)
    {
        var y = 0.0;

        foreach (var child in context.Children)
        {
            var childHeight = Math.Round(child.DesiredSize.Height);
            // Snap Y position and height to whole pixels to prevent text aliasing. 
            // Explicitly cast to float if the compiler requires it for the Rect constructor.
            child.Arrange(new Rect(0, (float)y, (float)finalSize.Width, (float)childHeight));
            y += childHeight;
        }

        return new Size(finalSize.Width, y);
    }
}

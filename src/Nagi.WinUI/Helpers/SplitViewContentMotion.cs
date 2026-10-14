using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Nagi.WinUI.Helpers;

internal sealed class SplitViewContentMotion
{
    private readonly SplitView _splitView;
    private readonly List<(DoubleAnimationUsingKeyFrames Animation, string From, string To)> _animations = [];

    private SplitViewContentMotion(SplitView splitView, FrameworkElement templateRoot, FrameworkElement contentRoot)
    {
        _splitView = splitView;
        foreach (var group in VisualStateManager.GetVisualStateGroups(templateRoot))
        {
            foreach (var transition in group.Transitions)
            {
                if (transition.Storyboard is not { } storyboard) continue;
                var animation = storyboard.Children.OfType<DoubleAnimationUsingKeyFrames>().FirstOrDefault(candidate =>
                    Storyboard.GetTargetName(candidate) == "ContentTransform" &&
                    Storyboard.GetTargetProperty(candidate) == "TranslateX");
                if (animation is null || animation.KeyFrames.Count == 0 ||
                    animation.KeyFrames[^1] is not SplineDoubleKeyFrame endFrame) continue;

                // Keep WinUI's pane transitions and named objects; replace only the inline content motion.
                animation.ClearValue(Storyboard.TargetNameProperty);
                Storyboard.SetTarget(animation, contentRoot);
                Storyboard.SetTargetProperty(animation, nameof(FrameworkElement.Width));
                animation.EnableDependentAnimation = true;
                // Hold the final bounds until WinUI commits the destination state.
                animation.FillBehavior = FillBehavior.HoldEnd;
                animation.KeyFrames.Clear();
                animation.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero) });
                animation.KeyFrames.Add(endFrame);
                _animations.Add((animation, transition.From, transition.To));

                for (var j = storyboard.Children.Count - 1; j >= 0; j--)
                {
                    var layout = storyboard.Children[j];
                    if (Storyboard.GetTargetName(layout) == "ContentRoot" &&
                        Storyboard.GetTargetProperty(layout) is "(Grid.Column)" or "(Grid.ColumnSpan)")
                        storyboard.Children.RemoveAt(j);
                }

                // Use the full viewport during the transition so the final inline column cannot clip it early.
                foreach (var (property, value) in new (string, object)[]
                {
                    (nameof(FrameworkElement.HorizontalAlignment), splitView.PanePlacement == SplitViewPanePlacement.Left
                        ? HorizontalAlignment.Right : HorizontalAlignment.Left),
                    ("(Grid.Column)", 0),
                    ("(Grid.ColumnSpan)", 2)
                })
                {
                    var layout = new ObjectAnimationUsingKeyFrames
                    {
                        Duration = new Duration(endFrame.KeyTime.TimeSpan),
                        FillBehavior = FillBehavior.HoldEnd
                    };
                    layout.KeyFrames.Add(new DiscreteObjectKeyFrame
                    {
                        KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = value
                    });
                    Storyboard.SetTarget(layout, contentRoot);
                    Storyboard.SetTargetProperty(layout, property);
                    storyboard.Children.Add(layout);
                }
            }
        }
        splitView.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width != e.PreviousSize.Width) UpdateBounds();
        };
        splitView.RegisterPropertyChangedCallback(SplitView.OpenPaneLengthProperty, (_, _) => UpdateBounds());
        splitView.RegisterPropertyChangedCallback(SplitView.CompactPaneLengthProperty, (_, _) => UpdateBounds());
        UpdateBounds();
    }

    private double ContentWidth(string state)
    {
        var paneWidth = state.StartsWith("OpenInline", StringComparison.Ordinal) ? _splitView.OpenPaneLength
            : state.StartsWith("ClosedCompact", StringComparison.Ordinal) ? _splitView.CompactPaneLength : 0;
        return Math.Max(0, _splitView.ActualWidth - paneWidth);
    }

    private void UpdateBounds()
    {
        foreach (var (animation, from, to) in _animations)
        {
            animation.KeyFrames[0].Value = ContentWidth(from);
            animation.KeyFrames[1].Value = ContentWidth(to);
        }
    }

    public static SplitViewContentMotion? Attach(SplitView splitView)
    {
        splitView.ApplyTemplate();
        if (VisualTreeHelper.GetChildrenCount(splitView) == 0 ||
            VisualTreeHelper.GetChild(splitView, 0) is not FrameworkElement templateRoot ||
            templateRoot.FindName("ContentRoot") is not FrameworkElement contentRoot) return null;
        return new SplitViewContentMotion(splitView, templateRoot, contentRoot);
    }
}

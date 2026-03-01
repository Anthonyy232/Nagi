using System;
using System.ComponentModel;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Nagi.Core.Models.Lyrics;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.ViewModels;

namespace Nagi.WinUI.Pages;

/// <summary>
///     A page that displays synchronized lyrics for the currently playing song.
/// </summary>
public sealed partial class LyricsPage : Page
{
    private const double ScrollIntoViewRatio = 0.30;
    private const int AnimationDurationMs = 300;
    private const float ActiveScale = 1.03f;
    private const float InactiveScale = 1.0f;
    private static readonly double[] _opacityCurve;
    private static readonly SolidColorBrush _transparentBrush = new(Microsoft.UI.Colors.Transparent);
    private static Brush? _hoverBrush;
    private readonly ILogger<LyricsPage> _logger;
    private readonly Storyboard _progressBarStoryboard = new();
    private bool _isUnloaded;

    /// <summary>
    ///     Controls whether the next lyric line activation should snap immediately instead of animating.
    ///     Set to true on page load and whenever new lyrics arrive (HasLyrics changed).
    ///     Consumed and reset to false on the first CurrentLine change that triggers a scroll.
    /// </summary>
    private bool _isInitialScroll = true;

    /// <summary>
    ///     Tracks the index of the last line whose overlay was set to active (opacity 1.0).
    ///     Used to guarantee the previous active overlay is always reset.
    /// </summary>
    private int _lastActiveOverlayIndex = -1;

    /// <summary>
    ///     Cached index of ViewModel.CurrentLine within LyricLines.
    ///     Kept in sync whenever CurrentLine changes to avoid repeated O(n) IndexOf calls
    ///     in hot-path handlers like ElementPrepared.
    /// </summary>
    private int _currentLineIndex = -1;

    static LyricsPage()
    {
        _opacityCurve = new double[20];
        for (var i = 0; i < _opacityCurve.Length; i++)
        {
            _opacityCurve[i] = Math.Max(0.05, Math.Pow(0.55, i));
        }
    }

    public LyricsPage()
    {
        ViewModel = App.Services!.GetRequiredService<LyricsPageViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<LyricsPage>>();
        InitializeComponent();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Unloaded += OnPageUnloaded;
        _logger.LogDebug("LyricsPage initialized.");
    }

    public LyricsPageViewModel ViewModel { get; }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _logger.LogDebug("LyricsPage loaded.");
        if (Resources["PageLoadStoryboard"] is Storyboard storyboard) storyboard.Begin();
        UpdateProgressBarForCurrentLine();

        // If lyrics are already loaded (navigated mid-song), snap to current line
        if (ViewModel.CurrentLine != null)
        {
            _isInitialScroll = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isUnloaded) return;
                // CurrentLine may have changed by the time this runs; recompute the index.
                _currentLineIndex = ViewModel.LyricLines.IndexOf(ViewModel.CurrentLine!);
                ApplyAllLineVisuals();
                ScrollToCurrentLine(disableAnimation: true);
            });
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _isUnloaded = true;
        _logger.LogDebug("LyricsPage unloaded. Cleaning up resources.");
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        Unloaded -= OnPageUnloaded;
        _progressBarStoryboard.Stop();
        ViewModel.Dispose();
    }

    /// <summary>
    ///     Responds to property changes in the ViewModel to update the UI accordingly.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ViewModel.CurrentLine):
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_isUnloaded) return;

                    // Keep cache in sync before any visual method reads it.
                    _currentLineIndex = ViewModel.CurrentLine != null
                        ? ViewModel.LyricLines.IndexOf(ViewModel.CurrentLine)
                        : -1;

                    // On first line change after lyrics load, snap immediately
                    if (_isInitialScroll)
                    {
                        _isInitialScroll = false;
                        ApplyAllLineVisuals();
                        ScrollToCurrentLine(disableAnimation: true);
                    }
                    else
                    {
                        AnimateLineTransition();
                        ScrollToCurrentLine(disableAnimation: false);
                    }

                    UpdateProgressBarForCurrentLine();
                });
                break;

            case nameof(ViewModel.IsPlaying):
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_isUnloaded) return;
                    if (ViewModel.IsPlaying)
                        UpdateProgressBarForCurrentLine();
                    else
                        _progressBarStoryboard.Pause();
                });
                break;

            case nameof(ViewModel.HasLyrics):
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_isUnloaded) return;
                    if (ViewModel.HasLyrics)
                    {
                        _isInitialScroll = true;
                        _lastActiveOverlayIndex = -1;
                        _currentLineIndex = -1;
                    }
                });
                break;
        }
    }

    /// <summary>
    ///     Called when an ItemsRepeater element is prepared (created or recycled).
    ///     Sets the initial Composition visual state so the overlay starts hidden
    ///     and the correct opacity/scale is applied based on whether it's the active line.
    ///     IMPORTANT: Opacity is set on individual TextBlocks, NOT the Grid container,
    ///     because sub-1.0 opacity on a container forces WinUI to render to an intermediate
    ///     bitmap surface which breaks ClearType text anti-aliasing.
    /// </summary>
    private void LyricsRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not Grid grid) return;

        // Use the cached index — avoids O(n) IndexOf on every element materialization.
        var currentIndex = _currentLineIndex;

        var distance = currentIndex >= 0 ? Math.Abs(args.Index - currentIndex) : int.MaxValue;
        var targetOpacity = GetOpacityForDistance(distance);
        var isActive = args.Index == currentIndex;

        // Set scale immediately on the grid (scale doesn't cause ClearType issues)
        var visual = ElementCompositionPreview.GetElementVisual(grid);
        var scale = isActive ? ActiveScale : InactiveScale;
        visual.Scale = new Vector3(scale, scale, 1.0f);

        // Set opacity on individual TextBlocks, NOT the grid
        if (grid.Children.Count >= 2)
        {
            if (grid.Children[0] is TextBlock baseText)
                CompositionAnimationHelper.SetOpacityImmediate(baseText, (float)targetOpacity);
            if (grid.Children[1] is TextBlock overlayText)
                CompositionAnimationHelper.SetOpacityImmediate(overlayText, isActive ? 1.0f : 0.0f);
        }
    }

    /// <summary>
    ///     Called when an element is being cleared/recycled. Resets Composition state.
    /// </summary>
    private void LyricsRepeater_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is not Grid grid) return;

        // Reset scale on grid and stop animations
        var visual = ElementCompositionPreview.GetElementVisual(grid);
        visual.StopAnimation("Scale");
        visual.Scale = new Vector3(1.0f, 1.0f, 1.0f);

        // Reset opacity on individual TextBlocks and stop animations
        if (grid.Children.Count >= 2)
        {
            if (grid.Children[0] is TextBlock baseText)
            {
                var baseVisual = ElementCompositionPreview.GetElementVisual(baseText);
                baseVisual.StopAnimation("Opacity");
                baseVisual.Opacity = 1.0f;
            }
            if (grid.Children[1] is TextBlock overlayText)
            {
                var overlayVisual = ElementCompositionPreview.GetElementVisual(overlayText);
                overlayVisual.StopAnimation("Opacity");
                overlayVisual.Opacity = 0.0f;
            }
        }
    }

    /// <summary>
    ///     Smoothly scrolls the lyrics to bring the current active line into view.
    ///     Uses ChangeView which replaces ongoing scroll animations rather than queuing them.
    /// </summary>
    private void ScrollToCurrentLine(bool disableAnimation)
    {
        // Use the cached index — _currentLineIndex is always updated before this is called.
        var lineIndex = _currentLineIndex;
        if (lineIndex < 0) return;

        var element = LyricsRepeater.TryGetElement(lineIndex);
        if (element == null)
        {
            _logger.LogDebug("ScrollToCurrentLine: element at index {Index} is null (unexpected with non-virtualizing layout).", lineIndex);
            return;
        }

        _logger.LogTrace("Scrolling to lyric line index {LineIndex}.", lineIndex);

        // Get position of element relative to the LyricsRepeater (its immediate visual parent in the StackPanel).
        // Because TopSpacer is a *sibling* of LyricsRepeater — not a parent — its height is already baked
        // into the ScrollViewer's content offset but NOT into the transform returned here.
        // Therefore position.Y == the exact VerticalOffset needed to place the element at the very top
        // of the viewport. TopSpacer.Height = viewportHeight * ScrollIntoViewRatio then pushes the
        // visible top down by that ratio, so the active line naturally lands at ScrollIntoViewRatio
        // from the top without any additional arithmetic.
        var transform = element.TransformToVisual(LyricsRepeater);
        var position = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
        var targetOffset = position.Y;

        // Clamp to valid scroll range
        var maxOffset = LyricsScrollViewer.ScrollableHeight;
        targetOffset = Math.Max(0, Math.Min(targetOffset, maxOffset));

        LyricsScrollViewer.ChangeView(null, targetOffset, null, disableAnimation);
    }

    /// <summary>
    ///     Sets spacer heights when the ScrollViewer is resized.
    /// </summary>
    private void LyricsScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var viewportHeight = e.NewSize.Height;
        TopSpacer.Height = viewportHeight * ScrollIntoViewRatio;
        BottomSpacer.Height = viewportHeight * (1.0 - ScrollIntoViewRatio);
    }

    /// <summary>
    ///     Animates the transition between the previous and current active lines.
    ///     Opacity is animated on individual TextBlocks to preserve ClearType rendering.
    /// </summary>
    private void AnimateLineTransition()
    {
        var currentIndex = _currentLineIndex;

        // Compute the union of both influence windows in a single pass to avoid calling
        // AnimateOpacity/AnimateScale twice on the same element (which would stop and restart
        // the animation mid-frame, causing a visible flicker on sequential line changes).
        var hasLast = _lastActiveOverlayIndex >= 0;
        var hasCurrent = currentIndex >= 0;

        if (!hasLast && !hasCurrent) return;

        var windowStart = hasLast && hasCurrent
            ? Math.Min(_lastActiveOverlayIndex, currentIndex) - 6
            : hasLast ? _lastActiveOverlayIndex - 6 : currentIndex - 6;
        var windowEnd = hasLast && hasCurrent
            ? Math.Max(_lastActiveOverlayIndex, currentIndex) + 6
            : hasLast ? _lastActiveOverlayIndex + 6 : currentIndex + 6;

        UpdateLinesInRange(
            Math.Max(0, windowStart),
            Math.Min(ViewModel.LyricLines.Count - 1, windowEnd),
            currentIndex, true);

        _lastActiveOverlayIndex = currentIndex;
    }

    /// <summary>
    ///     Sets all line visuals immediately (no animation). Used for initial load and track changes.
    /// </summary>
    private void ApplyAllLineVisuals()
    {
        var currentIndex = _currentLineIndex;

        if (ViewModel.LyricLines.Count > 0)
        {
            UpdateLinesInRange(0, ViewModel.LyricLines.Count - 1, currentIndex, false);
        }

        _lastActiveOverlayIndex = currentIndex;
    }

    /// <summary>
    ///     Updates the visual state of a specific range of lyric lines.
    /// </summary>
    private void UpdateLinesInRange(int minIndex, int maxIndex, int currentIndex, bool animate)
    {
        for (var i = minIndex; i <= maxIndex; i++)
        {
            var element = LyricsRepeater.TryGetElement(i);
            if (element is not Grid grid) continue;

            var distance = currentIndex >= 0 ? Math.Abs(i - currentIndex) : int.MaxValue;
            var targetOpacity = GetOpacityForDistance(distance);
            var isActive = i == currentIndex;

            // Update opacity on text blocks
            if (grid.Children.Count >= 2)
            {
                if (grid.Children[0] is TextBlock baseText)
                {
                    if (animate)
                        CompositionAnimationHelper.AnimateOpacity(baseText, (float)targetOpacity, AnimationDurationMs);
                    else
                        CompositionAnimationHelper.SetOpacityImmediate(baseText, (float)targetOpacity);
                }

                if (grid.Children[1] is TextBlock overlayText)
                {
                    if (animate)
                        CompositionAnimationHelper.AnimateOpacity(overlayText, isActive ? 1.0f : 0.0f, AnimationDurationMs);
                    else
                        CompositionAnimationHelper.SetOpacityImmediate(overlayText, isActive ? 1.0f : 0.0f);
                }
            }

            // Update scale on grid
            var targetScale = isActive ? ActiveScale : InactiveScale;
            if (animate)
            {
                CompositionAnimationHelper.AnimateScale(grid, targetScale, AnimationDurationMs);
            }
            else
            {
                var visual = ElementCompositionPreview.GetElementVisual(grid);
                var width = (float)grid.ActualWidth;
                var height = (float)grid.ActualHeight;
                if (width > 0 && height > 0)
                    visual.CenterPoint = new Vector3(width / 2f, height / 2f, 0f);
                visual.Scale = new Vector3(targetScale, targetScale, 1.0f);
            }
        }
    }

    /// <summary>
    ///     Gets the target opacity for a lyric line based on its distance from the active line.
    /// </summary>
    private static double GetOpacityForDistance(int distance)
    {
        return distance < _opacityCurve.Length ? _opacityCurve[distance] : 0.05;
    }

    /// <summary>
    ///     Handles pointer entering a lyric line for hover effect.
    /// </summary>
    private void LyricItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Grid grid) return;

        // Cache the hover brush on first use — the resource dictionary lookup only runs once
        // per page lifetime rather than on every pointer-enter event.
        _hoverBrush ??= Application.Current.Resources["SubtleFillColorSecondaryBrush"] as Brush;
        if (_hoverBrush != null)
            grid.Background = _hoverBrush;
    }

    /// <summary>
    ///     Handles pointer leaving a lyric line to remove hover effect.
    /// </summary>
    private void LyricItem_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Grid grid) return;

        // Use a cached brush — avoids allocating a new SolidColorBrush on every pointer exit.
        // Transparent (not null) is required to keep hit-testing active over the padded Grid area.
        grid.Background = _transparentBrush;
    }

    /// <summary>
    ///     Handles tapping a lyric line to seek to that position in the song.
    /// </summary>
    private void LyricItem_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is Grid grid && grid.Tag is LyricLine clickedLine)
        {
            _logger.LogDebug("User tapped lyric line at {Timestamp}. Seeking.", clickedLine.StartTime);
            _ = ViewModel.SeekToLineAsync(clickedLine);
        }
    }

    /// <summary>
    ///     Resets and starts the progress bar animation for the current lyric line.
    /// </summary>
    private void UpdateProgressBarForCurrentLine()
    {
        _progressBarStoryboard.Stop();
        _progressBarStoryboard.Children.Clear();

        var currentLine = ViewModel.CurrentLine;
        if (currentLine == null)
        {
            LyricsProgressBar.Value = 0;
            return;
        }

        _logger.LogTrace("Updating progress bar for line: {LyricText}", currentLine.Text);

        // _currentLineIndex is always kept in sync with CurrentLine on the dispatcher thread,
        // and UpdateProgressBarForCurrentLine is only called from that same thread.
        var currentIndex = _currentLineIndex;
        var nextLineStartTime = currentIndex >= 0 && currentIndex < ViewModel.LyricLines.Count - 1
            ? ViewModel.LyricLines[currentIndex + 1].StartTime
            : ViewModel.SongDuration;
        var lineDuration = nextLineStartTime - currentLine.StartTime;

        if (lineDuration <= TimeSpan.Zero)
        {
            LyricsProgressBar.Value = 100;
            return;
        }

        var positionInLine = ViewModel.CurrentPosition - currentLine.StartTime;
        if (positionInLine < TimeSpan.Zero) positionInLine = TimeSpan.Zero;

        var startValue = positionInLine.TotalMilliseconds / lineDuration.TotalMilliseconds * 100;
        LyricsProgressBar.Value = Math.Clamp(startValue, 0, 100);

        var remainingDuration = lineDuration - positionInLine;
        if (remainingDuration <= TimeSpan.Zero) return;

        var animation = new DoubleAnimation
        {
            To = 100.0,
            Duration = remainingDuration,
            EnableDependentAnimation = true
        };

        Storyboard.SetTarget(animation, LyricsProgressBar);
        Storyboard.SetTargetProperty(animation, nameof(ProgressBar.Value));
        _progressBarStoryboard.Children.Add(animation);

        _progressBarStoryboard.Begin();

        if (!ViewModel.IsPlaying) _progressBarStoryboard.Pause();
    }
}

using System;
using System.ComponentModel;
using System.Numerics;
using Windows.UI.ViewManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Nagi.Core.Models.Lyrics;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.ViewModels;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.Pages;

public sealed partial class LyricsPage : Page
{
    private const double ScrollIntoViewRatio = 0.30;
    private const int AnimationDurationMs = 300;
    private const float ActiveScale = 1.03f;
    private const float InactiveScale = 1.0f;
    private static readonly SolidColorBrush _transparentBrush = new(Microsoft.UI.Colors.Transparent);
    private readonly ILogger<LyricsPage> _logger;
    private readonly Storyboard _progressBarStoryboard = new();
    private readonly DoubleAnimation _progressBarAnimation = new() { To = 100, EnableDependentAnimation = true };
    private bool _isUnloaded;

    /// <summary>
    ///     Controls whether the next lyric line activation should snap immediately instead of animating.
    ///     Set to true on page load and whenever new lyrics arrive (HasLyrics changed).
    ///     Consumed and reset to false on the first CurrentLine change that triggers a scroll.
    /// </summary>
    private bool _isInitialScroll = true;

    private int _lastActiveOverlayIndex = -1;

    /// <summary>
    ///     Cached index of ViewModel.CurrentLine within LyricLines.
    ///     Kept in sync whenever CurrentLine changes to avoid repeated O(n) IndexOf calls
    ///     in hot-path handlers like ElementPrepared.
    /// </summary>
    private int _currentLineIndex = -1;

    public LyricsPage()
    {
        ViewModel = App.Services!.GetRequiredService<IViewModelFactory>().Create<LyricsPageViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<LyricsPage>>();
        InitializeComponent();
        Storyboard.SetTarget(_progressBarAnimation, LyricsProgressBar);
        Storyboard.SetTargetProperty(_progressBarAnimation, nameof(ProgressBar.Value));
        _progressBarStoryboard.Children.Add(_progressBarAnimation);

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Unloaded += OnPageUnloaded;
        _logger.LogDebug("LyricsPage initialized.");
    }

    public LyricsPageViewModel ViewModel { get; }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _logger.LogDebug("LyricsPage loaded.");
        CompositionAnimationHelper.AnimationsEnabledChanged += OnAnimationsEnabledChanged;
        if (Resources["PageLoadStoryboard"] is Storyboard storyboard)
        {
            storyboard.Begin();
            if (!CompositionAnimationHelper.AreAnimationsEnabled) storyboard.SkipToFill();
        }
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

    private async void EditLyricsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.EditLyricsCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to edit lyrics.");
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _isUnloaded = true;
        _logger.LogDebug("LyricsPage unloaded. Cleaning up resources.");
        CompositionAnimationHelper.AnimationsEnabledChanged -= OnAnimationsEnabledChanged;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        Unloaded -= OnPageUnloaded;
        _progressBarStoryboard.Stop();
        ViewModel.Dispose();
    }

    private void OnAnimationsEnabledChanged(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isUnloaded) return;
            ApplyAllLineVisuals();
            ScrollToCurrentLine(disableAnimation: true);
            UpdateProgressBarForCurrentLine();
        });
    }

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
            case nameof(ViewModel.CurrentPosition):
            case nameof(ViewModel.SongDuration):
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_isUnloaded) return;
                    UpdateProgressBarForCurrentLine();
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

    private void LyricsRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not Grid grid) return;

        ApplyLineVisual(grid, args.Index, _currentLineIndex, false);
    }

    private void LyricsRepeater_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is not Grid grid) return;

        var visual = ElementCompositionPreview.GetElementVisual(grid);
        visual.StopAnimation("Scale");
        visual.Scale = new Vector3(1.0f, 1.0f, 1.0f);

        SetLayerOpacity((Panel)grid.Children[0], 1.0f, false);
        SetLayerOpacity((Panel)grid.Children[1], 0.0f, false);
    }

    private void ScrollToCurrentLine(bool disableAnimation)
    {
        // Use the cached index — _currentLineIndex is always updated before this is called.
        var lineIndex = _currentLineIndex;
        if (lineIndex < 0) return;

        var element = LyricsRepeater.TryGetElement(lineIndex);
        if (element == null)
        {
            _logger.LogDebug("Lyric element at index {Index} has not been realized yet.", lineIndex);
            return;
        }

        _logger.LogTrace("Scrolling to lyric line index {LineIndex}.", lineIndex);

        // TopSpacer is a sibling, so the repeater-relative Y offset already places the line
        // at ScrollIntoViewRatio without subtracting the spacer again.
        var transform = element.TransformToVisual(LyricsRepeater);
        var position = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
        var targetOffset = position.Y;

        var maxOffset = LyricsScrollViewer.ScrollableHeight;
        targetOffset = Math.Clamp(targetOffset, 0, maxOffset);

        LyricsScrollViewer.ChangeView(null, targetOffset, null, disableAnimation || !CompositionAnimationHelper.AreAnimationsEnabled);
    }

    private void LyricsScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var viewportHeight = e.NewSize.Height;
        TopSpacer.Height = viewportHeight * ScrollIntoViewRatio;
        BottomSpacer.Height = viewportHeight * (1.0 - ScrollIntoViewRatio);
    }

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

    private void ApplyAllLineVisuals()
    {
        var currentIndex = _currentLineIndex;

        UpdateLinesInRange(0, ViewModel.LyricLines.Count - 1, currentIndex, false);

        _lastActiveOverlayIndex = currentIndex;
    }

    private void UpdateLinesInRange(int minIndex, int maxIndex, int currentIndex, bool animate)
    {
        for (var i = minIndex; i <= maxIndex; i++)
            if (LyricsRepeater.TryGetElement(i) is Grid grid)
                ApplyLineVisual(grid, i, currentIndex, animate);
    }

    private static void ApplyLineVisual(Grid grid, int index, int currentIndex, bool animate)
    {
        var distance = currentIndex >= 0 ? Math.Abs(index - currentIndex) : int.MaxValue;
        var isActive = index == currentIndex;
        SetLayerOpacity((Panel)grid.Children[0], (float)GetOpacityForDistance(distance), animate);
        SetLayerOpacity((Panel)grid.Children[1], isActive ? 1.0f : 0.0f, animate);
        var scale = isActive ? ActiveScale : InactiveScale;
        if (animate)
            CompositionAnimationHelper.AnimateScale(grid, scale, AnimationDurationMs);
        else
        {
            var visual = ElementCompositionPreview.GetElementVisual(grid);
            visual.StopAnimation("Scale");
            visual.CenterPoint = new Vector3((float)grid.ActualWidth / 2, (float)grid.ActualHeight / 2, 0);
            visual.Scale = new Vector3(scale, scale, 1);
        }
    }

    private static void SetLayerOpacity(Panel layer, float opacity, bool animate)
    {
        // Opacity on each TextBlock preserves ClearType; fading the panel rasterizes its children.
        foreach (var element in layer.Children)
        {
            if (animate) CompositionAnimationHelper.AnimateOpacity(element, opacity, AnimationDurationMs);
            else CompositionAnimationHelper.SetOpacityImmediate(element, opacity);
        }
    }

    private static double GetOpacityForDistance(int distance)
    {
        return Math.Max(0.05, Math.Pow(0.55, distance));
    }

    private void LyricItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Grid grid) return;

        grid.Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
    }

    private void LyricItem_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Grid grid) return;

        // Use a cached brush — avoids allocating a new SolidColorBrush on every pointer exit.
        // Transparent (not null) is required to keep hit-testing active over the padded Grid area.
        grid.Background = _transparentBrush;
    }

    private async void LyricItem_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is Grid grid && grid.Tag is LyricLine clickedLine)
        {
            _logger.LogDebug("User tapped lyric line at {Timestamp}. Seeking.", clickedLine.StartTime);
            try { await ViewModel.SeekToLineAsync(clickedLine); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to seek to lyric line"); }
        }
    }

    private void UpdateProgressBarForCurrentLine()
    {
        _progressBarStoryboard.Stop();

        var currentLine = ViewModel.CurrentLine;
        if (currentLine == null)
        {
            LyricsProgressBar.Value = 0;
            return;
        }

        _logger.LogTrace("Updating progress bar for line: {LyricText}", currentLine.Text);

        var currentIndex = ViewModel.LyricLines.IndexOf(currentLine);
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
        if (remainingDuration <= TimeSpan.Zero || !CompositionAnimationHelper.AreAnimationsEnabled) return;

        _progressBarAnimation.Duration = remainingDuration;

        _progressBarStoryboard.Begin();

        if (!ViewModel.IsPlaying) _progressBarStoryboard.Pause();
    }
}

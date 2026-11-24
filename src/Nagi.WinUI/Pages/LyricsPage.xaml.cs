using System.ComponentModel;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Nagi.Core.Lyrics;
using Nagi.Presentation.Lyrics;
using Nagi.Presentation.Player;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.Pages;

public sealed partial class LyricsPage : ViewModelPage
{
    private const double ScrollIntoViewRatio = 0.30;
    private const int AnimationDurationMs = 300;
    private const float ActiveScale = 1.03f;
    private const float InactiveScale = 1.0f;

    // Lines this far from the old or new current line are restyled when the current line moves.
    private const int InfluenceRadius = 6;

    private const string BaseLayerName = "BaseLayer";
    private const string ActiveLayerName = "ActiveLayer";
    private const string HoverBackgroundName = "HoverBackground";
    private readonly Storyboard _progressBarStoryboard = new();
    private readonly DoubleAnimation _progressBarAnimation = new() { To = 100, EnableDependentAnimation = true };
    private readonly SolidColorBrush _unsungWordBrush = new(Colors.Transparent);
    private readonly DispatcherQueueTimer _nextWordTimer;
    private bool _isUnloaded;

    // The first current line of newly loaded lyrics is jumped to rather than animated to.
    private bool _isInitialScroll = true;

    private int _lastActiveOverlayIndex = -1;

    public LyricsPage()
    {
        ViewModel = GetViewModel<LyricsPageViewModel>();
        Player = GetViewModel<PlayerViewModel>();
        InitializeComponent();
        Storyboard.SetTarget(_progressBarAnimation, LyricsProgressBar);
        Storyboard.SetTargetProperty(_progressBarAnimation, nameof(ProgressBar.Value));
        _progressBarStoryboard.Children.Add(_progressBarAnimation);
        _nextWordTimer = DispatcherQueue.CreateTimer();
        _nextWordTimer.IsRepeating = false;
        _nextWordTimer.Tick += (_, _) => UpdateSungWords();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.LyricsReplaced += OnLyricsReplaced;
        Unloaded += OnPageUnloaded;
    }

    public LyricsPageViewModel ViewModel { get; }

    public PlayerViewModel Player { get; }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        SystemVisualSettings.AnimationsEnabledChanged += OnAnimationsEnabledChanged;
        Player.PropertyChanged += OnPlayerPropertyChanged;
        UpdateFullScreenState();
        if (Resources["PageLoadStoryboard"] is Storyboard storyboard)
        {
            storyboard.Begin();
            if (!SystemVisualSettings.AnimationsEnabled) storyboard.SkipToFill();
        }
        UpdateCurrentLineProgress();

        // If lyrics are already loaded (navigated mid-song), snap to current line
        if (ViewModel.CurrentLine != null)
        {
            _isInitialScroll = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isUnloaded) return;
                ApplyAllLineVisuals();
                ScrollToCurrentLine(disableAnimation: true);
                UpdateSungWords();
            });
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _isUnloaded = true;
        SystemVisualSettings.AnimationsEnabledChanged -= OnAnimationsEnabledChanged;
        Player.PropertyChanged -= OnPlayerPropertyChanged;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.LyricsReplaced -= OnLyricsReplaced;
        Unloaded -= OnPageUnloaded;
        _progressBarStoryboard.Stop();
        _nextWordTimer.Stop();
    }

    private void OnAnimationsEnabledChanged(bool isEnabled)
    {
        ApplyAllLineVisuals();
        ScrollToCurrentLine(disableAnimation: true);
        UpdateCurrentLineProgress();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ViewModel.CurrentLine):
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_isUnloaded) return;

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

                    UpdateCurrentLineProgress();
                });
                break;

            case nameof(ViewModel.ShowNoLyricsMessage):
                UpdateFullScreenState();
                break;

            case nameof(ViewModel.IsPlaying):
            case nameof(ViewModel.CurrentPosition):
            case nameof(ViewModel.SongDuration):
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_isUnloaded) return;
                    UpdateCurrentLineProgress();
                });
                break;
        }
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Player.IsFullScreen)) UpdateFullScreenState();
    }

    // Decoded at the size shown, so it is sharp on any screen.
    private void FullScreenArtworkArea_SizeChanged(object sender, SizeChangedEventArgs e) =>
        FullScreenArtwork.Size = Math.Min(e.NewSize.Width, e.NewSize.Height);

    private void UpdateFullScreenState() => VisualStateManager.GoToState(this,
        !Player.IsFullScreen ? "Windowed" : ViewModel.ShowNoLyricsMessage ? "FullScreenWithoutLyrics" : "FullScreen",
        false);

    private void OnLyricsReplaced()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _isInitialScroll = true;
            _lastActiveOverlayIndex = -1;
        });
    }

    private void LyricsRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not Grid grid) return;

        ApplyLineVisual(grid, args.Index, ViewModel.CurrentLineIndex, false);
    }

    private void LyricsRepeater_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is not Grid grid) return;

        var visual = ElementCompositionPreview.GetElementVisual(grid);
        visual.StopAnimation("Scale");
        visual.Scale = new Vector3(1.0f, 1.0f, 1.0f);

        SetLayerOpacity(Layer(grid, BaseLayerName), 1.0f, false);
        SetLayerOpacity(Layer(grid, ActiveLayerName), 0.0f, false);
        SetHoverVisible(grid, false);
    }

    private void ScrollToCurrentLine(bool disableAnimation)
    {
        var lineIndex = ViewModel.CurrentLineIndex;
        if (lineIndex < 0) return;

        var element = LyricsRepeater.TryGetElement(lineIndex);
        if (element == null) return;

        // TopSpacer is a sibling, so the repeater-relative Y offset already places the line
        // at ScrollIntoViewRatio without subtracting the spacer again.
        var transform = element.TransformToVisual(LyricsRepeater);
        var position = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
        var targetOffset = position.Y;

        var maxOffset = LyricsScrollViewer.ScrollableHeight;
        targetOffset = Math.Clamp(targetOffset, 0, maxOffset);

        LyricsScrollViewer.ChangeView(null, targetOffset, null, disableAnimation || !SystemVisualSettings.AnimationsEnabled);
    }

    private void LyricsScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var viewportHeight = e.NewSize.Height;
        TopSpacer.Height = viewportHeight * ScrollIntoViewRatio;
        BottomSpacer.Height = viewportHeight * (1.0 - ScrollIntoViewRatio);
    }

    private void AnimateLineTransition()
    {
        var currentIndex = ViewModel.CurrentLineIndex;
        var previousIndex = _lastActiveOverlayIndex;
        if (previousIndex < 0 && currentIndex < 0) return;

        // One pass over the union of both influence windows: animating a line twice would stop and restart its
        // animation mid-frame, which flickers when lines change in quick succession.
        var first = previousIndex < 0 ? currentIndex
            : currentIndex < 0 ? previousIndex
            : Math.Min(previousIndex, currentIndex);
        var last = Math.Max(previousIndex, currentIndex);

        UpdateLinesInRange(
            Math.Max(0, first - InfluenceRadius),
            Math.Min(ViewModel.LyricLines.Count - 1, last + InfluenceRadius),
            currentIndex, true);

        _lastActiveOverlayIndex = currentIndex;
    }

    private void ApplyAllLineVisuals()
    {
        var currentIndex = ViewModel.CurrentLineIndex;

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
        SetLayerOpacity(Layer(grid, BaseLayerName), (float)GetOpacityForDistance(distance), animate);
        SetLayerOpacity(Layer(grid, ActiveLayerName), isActive ? 1.0f : 0.0f, animate);
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

    private void LyricItem_PointerEntered(object sender, PointerRoutedEventArgs e) => SetHoverVisible(sender, true);

    private void LyricItem_PointerExited(object sender, PointerRoutedEventArgs e) => SetHoverVisible(sender, false);

    private static void SetHoverVisible(object sender, bool visible)
    {
        if (sender is Grid grid)
            ((UIElement)grid.FindName(HoverBackgroundName)).Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Panel Layer(Grid line, string name) => (Panel)line.FindName(name);

    private async void LyricItem_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is Grid grid && grid.Tag is LyricLine clickedLine)
        {
            await ViewModel.SeekToLineAsync(clickedLine);
        }
    }

    private void UpdateCurrentLineProgress()
    {
        UpdateSungWords();
        _progressBarStoryboard.Stop();

        var (fraction, remaining) = ViewModel.GetCurrentLineProgress();
        LyricsProgressBar.Value = fraction * 100;
        if (remaining <= TimeSpan.Zero || !SystemVisualSettings.AnimationsEnabled) return;

        _progressBarAnimation.Duration = remaining;
        _progressBarStoryboard.Begin();
        if (!ViewModel.IsPlaying) _progressBarStoryboard.Pause();
    }

    // Colours the words sung so far in a current line that times its words, and comes back when the next one starts.
    private void UpdateSungWords()
    {
        _nextWordTimer.Stop();
        if (ViewModel.CurrentLine is not { Words.Count: > 0 } line ||
            LyricsRepeater.TryGetElement(ViewModel.CurrentLineIndex) is not Grid grid) return;

        var (sungLength, nextWordIn) = ViewModel.GetCurrentLineSungText();

        // The words still to come are see-through, so they show the base layer's colour.
        var text = (TextBlock)Layer(grid, ActiveLayerName).Children[0];
        if (text.Inlines is not [Run sung, Run] || sung.Text.Length != sungLength)
        {
            text.Inlines.Clear();
            text.Inlines.Add(new Run { Text = line.Text[..sungLength] });
            text.Inlines.Add(new Run { Text = line.Text[sungLength..], Foreground = _unsungWordBrush });
        }

        if (!ViewModel.IsPlaying || nextWordIn is not { } delay) return;
        _nextWordTimer.Interval = delay;
        _nextWordTimer.Start();
    }
}

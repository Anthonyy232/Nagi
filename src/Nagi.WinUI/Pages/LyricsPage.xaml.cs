using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Nagi.Core.Models.Lyrics;
using Nagi.WinUI.ViewModels;

namespace Nagi.WinUI.Pages;

/// <summary>
///     A page that displays synchronized lyrics for the currently playing song.
/// </summary>
public sealed partial class LyricsPage : Page
{
    private const double ScrollIntoViewRatio = 0.40;
    private readonly ILogger<LyricsPage> _logger;
    private readonly Storyboard _progressBarStoryboard = new();
    private bool _isUnloaded;

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
                _logger.LogDebug("Current lyric line changed. Updating UI.");
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_isUnloaded) return;
                    ScrollToCurrentLine();
                    UpdateProgressBarForCurrentLine();
                });
                break;

            case nameof(ViewModel.IsPlaying):
                _logger.LogDebug("Playback state changed to IsPlaying: {IsPlaying}. Updating progress bar.",
                    ViewModel.IsPlaying);
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_isUnloaded) return;
                    if (ViewModel.IsPlaying)
                        UpdateProgressBarForCurrentLine();
                    else
                        _progressBarStoryboard.Pause();
                });
                break;
        }
    }

    /// <summary>
    ///     Handles clicks on a lyric line to seek to that position in the song.
    /// </summary>
    private void LyricsListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LyricLine clickedLine)
        {
            _logger.LogDebug("User clicked lyric line at {Timestamp}. Seeking.", clickedLine.StartTime);
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

    /// <summary>
    ///     Smoothly scrolls the lyrics list to bring the current active line into view.
    /// </summary>
    private async void ScrollToCurrentLine()
    {
        var lineToScrollTo = ViewModel.CurrentLine;
        if (lineToScrollTo == null) return;

        var lineIndex = ViewModel.LyricLines.IndexOf(lineToScrollTo);
        if (lineIndex < 0) return;

        _logger.LogTrace("Scrolling to lyric line index {LineIndex}.", lineIndex);

        var options = new BringIntoViewOptions
        {
            VerticalAlignmentRatio = ScrollIntoViewRatio,
            AnimationDesired = true
        };

        var container = LyricsListView.ContainerFromIndex(lineIndex) as UIElement;

        if (container != null)
        {
            container.StartBringIntoView(options);
        }
        else
        {
            LyricsListView.ScrollIntoView(lineToScrollTo);
            await Task.Yield();
            var newContainer = LyricsListView.ContainerFromIndex(lineIndex) as UIElement;
            newContainer?.StartBringIntoView(options);
        }
    }
}
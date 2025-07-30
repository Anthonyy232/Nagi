using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Nagi.Core.Models.Lyrics;
using Nagi.WinUI.ViewModels;
using System;
using System.ComponentModel;
using System.Threading.Tasks;

namespace Nagi.WinUI.Pages;

/// <summary>
/// A page that displays synchronized lyrics for the currently playing song.
/// It manages a storyboard to create a smooth, independent progress animation
/// for the current lyric line.
/// </summary>
public sealed partial class LyricsPage : Page {
    public LyricsPageViewModel ViewModel { get; }

    // Storyboard for animating the progress bar for the current lyric line.
    private readonly Storyboard _progressBarStoryboard = new();

    // Defines how far into the viewport the active line should be scrolled (25% from the top).
    private const double ScrollIntoViewRatio = 0.25;

    public LyricsPage() {
        ViewModel = App.Services!.GetRequiredService<LyricsPageViewModel>();
        this.InitializeComponent();

        // Event handlers are registered to respond to ViewModel changes and page lifecycle events.
        // They are unregistered in OnPageUnloaded to prevent memory leaks.
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        this.Unloaded += OnPageUnloaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e) {
        // Play the initial fade-in animation for the page content.
        if (this.Resources["PageLoadStoryboard"] is Storyboard storyboard) {
            storyboard.Begin();
        }
        UpdateProgressBarForCurrentLine();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e) {
        // Clean up resources to prevent memory leaks when the page is no longer in use.
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        this.Unloaded -= OnPageUnloaded;
        _progressBarStoryboard.Stop();
        ViewModel.Dispose();
    }

    /// <summary>
    /// Responds to property changes in the ViewModel to update the UI accordingly.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e) {
        switch (e.PropertyName) {
            // When the active lyric line changes, scroll it into view and reset the progress bar.
            case nameof(ViewModel.CurrentLine):
                DispatcherQueue.TryEnqueue(() => {
                    ScrollToCurrentLine();
                    UpdateProgressBarForCurrentLine();
                });
                break;

            // When playback state changes, pause or resume the progress bar animation.
            case nameof(ViewModel.IsPlaying):
                DispatcherQueue.TryEnqueue(() => {
                    if (ViewModel.IsPlaying) {
                        // If resuming, ensure the animation is running from the correct point.
                        UpdateProgressBarForCurrentLine();
                    }
                    else {
                        _progressBarStoryboard.Pause();
                    }
                });
                break;
        }
    }

    /// <summary>
    /// Handles clicks on a lyric line to seek to that position in the song.
    /// </summary>
    private void LyricsListView_ItemClick(object sender, ItemClickEventArgs e) {
        if (e.ClickedItem is LyricLine clickedLine) {
            // Asynchronously ask the ViewModel to seek to the clicked line's timestamp.
            _ = ViewModel.SeekToLineAsync(clickedLine);
        }
    }

    /// <summary>
    /// Resets and starts the progress bar animation for the current lyric line.
    /// This method calculates the line's duration and the song's current progress
    /// within that line to create a perfectly synchronized animation.
    /// </summary>
    private void UpdateProgressBarForCurrentLine() {
        // Stop any existing animation before creating a new one.
        _progressBarStoryboard.Stop();
        _progressBarStoryboard.Children.Clear();

        var currentLine = ViewModel.CurrentLine;
        if (currentLine == null) {
            LyricsProgressBar.Value = 0;
            return;
        }

        // Determine the duration of the current line by finding the start time of the next line.
        int currentIndex = ViewModel.LyricLines.IndexOf(currentLine);
        TimeSpan nextLineStartTime = (currentIndex >= 0 && currentIndex < ViewModel.LyricLines.Count - 1)
            ? ViewModel.LyricLines[currentIndex + 1].StartTime
            : ViewModel.SongDuration;
        TimeSpan lineDuration = nextLineStartTime - currentLine.StartTime;

        // If the line has no duration, fill the progress bar and stop.
        if (lineDuration <= TimeSpan.Zero) {
            LyricsProgressBar.Value = 100;
            return;
        }

        // Calculate how far into the current line the playback position is.
        var positionInLine = ViewModel.CurrentPosition - currentLine.StartTime;
        if (positionInLine < TimeSpan.Zero) positionInLine = TimeSpan.Zero;

        // Set the initial value of the progress bar.
        double startValue = (positionInLine.TotalMilliseconds / lineDuration.TotalMilliseconds) * 100;
        LyricsProgressBar.Value = Math.Clamp(startValue, 0, 100);

        var remainingDuration = lineDuration - positionInLine;
        if (remainingDuration <= TimeSpan.Zero) {
            return;
        }

        // Create a new animation to fill the rest of the progress bar over the remaining time.
        var animation = new DoubleAnimation {
            To = 100.0,
            Duration = remainingDuration,
            EnableDependentAnimation = true
        };

        Storyboard.SetTarget(animation, LyricsProgressBar);
        Storyboard.SetTargetProperty(animation, nameof(ProgressBar.Value));
        _progressBarStoryboard.Children.Add(animation);

        _progressBarStoryboard.Begin();

        // If playback is paused, the animation should also be paused.
        if (!ViewModel.IsPlaying) {
            _progressBarStoryboard.Pause();
        }
    }

    /// <summary>
    /// Smoothly scrolls the lyrics list to bring the current active line into view.
    /// </summary>
    private async void ScrollToCurrentLine() {
        var lineToScrollTo = ViewModel.CurrentLine;
        if (lineToScrollTo == null) return;

        int lineIndex = ViewModel.LyricLines.IndexOf(lineToScrollTo);
        if (lineIndex < 0) return;

        var options = new BringIntoViewOptions() {
            VerticalAlignmentRatio = ScrollIntoViewRatio,
            AnimationDesired = true
        };

        var container = LyricsListView.ContainerFromIndex(lineIndex) as UIElement;

        if (container != null) {
            // If the item container is already realized, bring it into view.
            container.StartBringIntoView(options);
        }
        else {
            // If the item is virtualized, first scroll to it to realize the container,
            // then apply the smooth BringIntoView animation.
            LyricsListView.ScrollIntoView(lineToScrollTo);
            await Task.Yield();

            var newContainer = LyricsListView.ContainerFromIndex(lineIndex) as UIElement;
            newContainer?.StartBringIntoView(options);
        }
    }
}
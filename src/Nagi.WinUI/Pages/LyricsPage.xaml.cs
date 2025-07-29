using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Nagi.WinUI.ViewModels;
using System;
using System.ComponentModel;
using System.Threading.Tasks;

namespace Nagi.WinUI.Pages;

/// <summary>
/// A page that displays synchronized lyrics for the currently playing song.
/// It manages a storyboard to create a smooth, independent progress animation.
/// </summary>
public sealed partial class LyricsPage : Page {
    public LyricsPageViewModel ViewModel { get; }
    private readonly Storyboard _progressBarStoryboard = new();

    public LyricsPage() {
        ViewModel = App.Services!.GetRequiredService<LyricsPageViewModel>();
        this.InitializeComponent();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        this.Unloaded += OnUnloaded;
    }

    /// <summary>
    /// Handles the page's Unloaded event to clean up resources.
    /// </summary>
    private void OnUnloaded(object sender, RoutedEventArgs e) {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _progressBarStoryboard.Stop();
        ViewModel.Dispose();
    }

    /// <summary>
    /// Handles the page's Loaded event to trigger animations and initial setup.
    /// </summary>
    private void LyricsPage_Loaded(object sender, RoutedEventArgs e) {
        if (this.Resources["PageLoadStoryboard"] is Storyboard storyboard) {
            storyboard.Begin();
        }
        UpdateProgressBarForCurrentLine();
    }

    /// <summary>
    /// Responds to property changes in the ViewModel to update the UI.
    /// </summary>
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e) {
        switch (e.PropertyName) {
            case nameof(ViewModel.CurrentLine):
                DispatcherQueue.TryEnqueue(() => {
                    ScrollToCurrentLine();
                    UpdateProgressBarForCurrentLine();
                });
                break;

            case nameof(ViewModel.IsPlaying):
                if (ViewModel.IsPlaying) {
                    // Resync the animation when playback starts or resumes
                    // to handle both simple resume and resuming after a seek.
                    UpdateProgressBarForCurrentLine();
                }
                else {
                    _progressBarStoryboard.Pause();
                }
                break;
        }
    }

    /// <summary>
    /// Creates and manages a smooth animation for the progress of the current lyric line.
    /// </summary>
    private void UpdateProgressBarForCurrentLine() {
        _progressBarStoryboard.Stop();
        _progressBarStoryboard.Children.Clear();

        var currentLine = ViewModel.CurrentLine;
        if (currentLine == null) {
            LyricsProgressBar.Value = 0;
            return;
        }

        // Determine the start time of the next line to calculate the current line's duration.
        int currentIndex = ViewModel.LyricLines.IndexOf(currentLine);
        TimeSpan nextLineStartTime;
        if (currentIndex >= 0 && currentIndex < ViewModel.LyricLines.Count - 1) {
            nextLineStartTime = ViewModel.LyricLines[currentIndex + 1].StartTime;
        }
        else {
            // For the last line, the end time is the song's total duration.
            nextLineStartTime = ViewModel.SongDuration;
        }

        TimeSpan lineDuration = nextLineStartTime - currentLine.StartTime;

        // Handle lines with no duration (e.g., same timestamp as the next line).
        if (lineDuration <= TimeSpan.Zero) {
            LyricsProgressBar.Value = ViewModel.CurrentPosition >= currentLine.StartTime ? 100 : 0;
            return;
        }

        // Calculate the starting progress value to handle seeking within a line.
        var positionInLine = ViewModel.CurrentPosition - currentLine.StartTime;
        double startValue = (positionInLine.TotalMilliseconds / lineDuration.TotalMilliseconds) * 100;
        startValue = Math.Clamp(startValue, 0, 100);

        // Calculate the remaining time to animate over.
        var remainingDuration = lineDuration - positionInLine;
        if (remainingDuration <= TimeSpan.Zero) {
            LyricsProgressBar.Value = 100;
            return;
        }

        // Create the animation from the calculated start point to 100%.
        var animation = new DoubleAnimation {
            From = startValue,
            To = 100.0,
            Duration = new Duration(remainingDuration),
            EnableDependentAnimation = true
        };

        Storyboard.SetTarget(animation, LyricsProgressBar);
        Storyboard.SetTargetProperty(animation, "Value");
        _progressBarStoryboard.Children.Add(animation);

        // Begin the animation and sync its state with the player.
        _progressBarStoryboard.Begin();
        if (!ViewModel.IsPlaying) {
            _progressBarStoryboard.Pause();
        }
    }

    /// <summary>
    /// Smoothly scrolls the lyrics list to bring the current line into view.
    /// </summary>
    private async void ScrollToCurrentLine() {
        if (ViewModel.CurrentLine == null) return;

        int lineIndex = ViewModel.LyricLines.IndexOf(ViewModel.CurrentLine);
        if (lineIndex < 0) return;

        var options = new BringIntoViewOptions() {
            VerticalAlignmentRatio = 0.25,
            AnimationDesired = true
        };

        var container = LyricsListView.ContainerFromIndex(lineIndex) as UIElement;

        if (container != null) {
            container.StartBringIntoView(options);
        }
        else {
            // Fallback for when the container is not yet realized.
            LyricsListView.ScrollIntoView(ViewModel.CurrentLine);
            await Task.Yield();
            container = LyricsListView.ContainerFromIndex(lineIndex) as UIElement;
            if (container != null) {
                container.StartBringIntoView(options);
            }
        }
    }
}
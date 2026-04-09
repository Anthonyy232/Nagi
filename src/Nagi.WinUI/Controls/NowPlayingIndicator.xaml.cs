using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace Nagi.WinUI.Controls;

/// <summary>
///     A small "now playing" indicator: 3 bouncing equalizer bars in the app accent color
///     overlaid on a semi-transparent dark scrim. Used to mark the currently-playing song
///     in song list rows.
/// </summary>
public sealed partial class NowPlayingIndicator : UserControl
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive),
        typeof(bool),
        typeof(NowPlayingIndicator),
        new PropertyMetadata(false, OnIsActiveChanged));

    public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(
        nameof(IsPlaying),
        typeof(bool),
        typeof(NowPlayingIndicator),
        new PropertyMetadata(true, OnIsPlayingChanged));

    /// <summary>
    ///     When true, the indicator is visible. When false, it is collapsed and the
    ///     animation is stopped to avoid wasted work on offscreen rows.
    /// </summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>
    ///     When true, the equalizer bars animate. When false, the bars freeze in place
    ///     (mirroring the player's paused state). Only meaningful when IsActive is true.
    /// </summary>
    public bool IsPlaying
    {
        get => (bool)GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public NowPlayingIndicator()
    {
        InitializeComponent();
        // The Storyboard is defined in XAML resources; ensure the initial state matches
        // the current property values once the control is loaded.
        Loaded += (_, _) => ApplyState();
        Unloaded += (_, _) => StopAnimation();
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((NowPlayingIndicator)d).ApplyState();
    }

    private static void OnIsPlayingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((NowPlayingIndicator)d).ApplyState();
    }

    private void ApplyState()
    {
        if (RootGrid is null) return;

        if (!IsActive)
        {
            RootGrid.Visibility = Visibility.Collapsed;
            StopAnimation();
            return;
        }

        RootGrid.Visibility = Visibility.Visible;
        if (IsPlaying)
        {
            StartOrResumeAnimation();
        }
        else
        {
            PauseAnimation();
        }
    }

    private void StartOrResumeAnimation()
    {
        var sb = EqualizerStoryboard;
        if (sb is null) return;

        // Storyboard.GetCurrentState reports Stopped/Paused/Active. From Stopped we Begin;
        // from Paused we Resume. Calling Begin while Active is a no-op restart that we
        // want to avoid (otherwise the bars hitch every time IsPlaying flips back on).
        var state = sb.GetCurrentState();
        if (state == ClockState.Stopped)
        {
            sb.Begin();
        }
        else
        {
            sb.Resume();
        }
    }

    private void PauseAnimation()
    {
        EqualizerStoryboard?.Pause();
    }

    private void StopAnimation()
    {
        EqualizerStoryboard?.Stop();
    }
}

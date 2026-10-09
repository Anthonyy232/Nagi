using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Nagi.WinUI.Helpers;
using Windows.UI.ViewManagement;

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
        new PropertyMetadata(false, OnPlaybackStateChanged));

    public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(
        nameof(IsPlaying),
        typeof(bool),
        typeof(NowPlayingIndicator),
        new PropertyMetadata(true, OnPlaybackStateChanged));

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
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CompositionAnimationHelper.AnimationsEnabledChanged += OnAnimationsEnabledChanged;
        ApplyState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CompositionAnimationHelper.AnimationsEnabledChanged -= OnAnimationsEnabledChanged;
        EqualizerStoryboard.Stop();
    }

    private void OnAnimationsEnabledChanged(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args) =>
        DispatcherQueue.TryEnqueue(ApplyState);

    private static void OnPlaybackStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((NowPlayingIndicator)d).ApplyState();
    }

    private void ApplyState()
    {
        if (!IsLoaded) return;

        if (!IsActive)
        {
            RootGrid.Visibility = Visibility.Collapsed;
            EqualizerStoryboard.Stop();
            return;
        }

        RootGrid.Visibility = Visibility.Visible;
        if (!CompositionAnimationHelper.AreAnimationsEnabled)
        {
            EqualizerStoryboard.Stop();
        }
        else if (IsPlaying)
        {
            if (EqualizerStoryboard.GetCurrentState() == ClockState.Stopped)
                EqualizerStoryboard.Begin();
            else
                EqualizerStoryboard.Resume();
        }
        else
        {
            EqualizerStoryboard.Pause();
        }
    }
}

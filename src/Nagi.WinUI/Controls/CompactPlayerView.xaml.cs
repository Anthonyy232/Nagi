using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Nagi.Presentation.Player;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.Controls;

/// <summary>
///     The small player shared by the mini player and the tray popup: the current song, the seek bar and the
///     transport buttons, or the songs up next. Each view switches between the two on its own.
/// </summary>
public sealed partial class CompactPlayerView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(PlayerViewModel), typeof(CompactPlayerView),
        new PropertyMetadata(null, (d, _) => ((CompactPlayerView)d).OnViewModelChanged()));

    public static readonly DependencyProperty HeaderTrailingContentProperty = DependencyProperty.Register(
        nameof(HeaderTrailingContent), typeof(object), typeof(CompactPlayerView), new PropertyMetadata(null));

    private IDisposable? _queuePreview;

    public CompactPlayerView()
    {
        InitializeComponent();
        Loaded += (_, _) => VisualStateManager.GoToState(this, IsOnImage ? "OnImage" : "OnTheme", false);
        Unloaded += (_, _) => ShowPlayer();
    }

    public PlayerViewModel? ViewModel
    {
        get => (PlayerViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    /// <summary>Light controls for showing the player over album art, as the mini player does.</summary>
    public bool IsOnImage { get; set; }

    /// <summary>Shown at the end of the header row, such as the mini player's restore button.</summary>
    public object? HeaderTrailingContent
    {
        get => GetValue(HeaderTrailingContentProperty);
        set => SetValue(HeaderTrailingContentProperty, value);
    }

    /// <summary>The parts that take pointer input, for a host that drags its window by the rest.</summary>
    public IReadOnlyList<FrameworkElement> InteractiveElements =>
        [MediaSeekerSlider, MediaControlsPanel, BackButton, QueueListView, Artists];

    /// <summary>Raised after the view switched between the player and the queue.</summary>
    public event Action? ViewChanged;

    public void ShowPlayer()
    {
        _queuePreview?.Dispose();
        _queuePreview = null;
        GoToView("PlayerView");
    }

    private void ShowQueue()
    {
        _queuePreview ??= ViewModel?.PreviewQueue();
        GoToView("QueueView");
    }

    private void GoToView(string state)
    {
        VisualStateManager.GoToState(this, state, false);
        ViewChanged?.Invoke();
    }

    private void OnViewModelChanged()
    {
        if (ViewModel is not { } viewModel) return;
        PlayerInputHelper.AttachSeekSlider(MediaSeekerSlider, viewModel);
        Bindings.Update();
    }

    private void QueueButton_Click(object sender, RoutedEventArgs e) => ShowQueue();

    private void BackButton_Click(object sender, RoutedEventArgs e) => ShowPlayer();

    private void VolumeButton_PointerWheelChanged(object sender, PointerRoutedEventArgs e) =>
        PlayerInputHelper.StepVolumeWithWheel(sender, e, ViewModel!);
}

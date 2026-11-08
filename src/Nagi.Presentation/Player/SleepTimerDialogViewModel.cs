using CommunityToolkit.Mvvm.ComponentModel;

namespace Nagi.Presentation.Player;

/// <summary>
///     The sleep timer dialog: how long until playback stops, and whether the playing track finishes first.
/// </summary>
public sealed partial class SleepTimerDialogViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAccept))]
    public partial double Minutes { get; set; } = ListeningMinutes.Default;

    [ObservableProperty] public partial bool FinishTrack { get; set; } = true;

    public bool CanAccept => ListeningMinutes.IsValid(Minutes);
}

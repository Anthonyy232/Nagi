namespace Nagi.Presentation.Player;

/// <summary>
///     The sleep timer dialog: how long until playback stops, and whether the playing track finishes first.
/// </summary>
public sealed class SleepTimerDialogViewModel
{
    public double Minutes { get; set; } = ListeningMinutes.Default;

    public bool FinishTrack { get; set; } = true;

    public bool CanAccept => ListeningMinutes.IsValid(Minutes);
}

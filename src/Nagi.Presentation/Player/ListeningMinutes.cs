using Nagi.Core.Playback;

namespace Nagi.Presentation.Player;

/// <summary>
///     The minutes the "Play for" and sleep timer dialogs accept.
/// </summary>
public static class ListeningMinutes
{
    public const int Default = 30;

    public static bool IsValid(double minutes) =>
        minutes is >= 1 and <= PlaybackLimits.MaxScheduledMinutes && minutes == Math.Truncate(minutes);
}

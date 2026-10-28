using Nagi.Core.Models;

namespace Nagi.Presentation.Helpers;

/// <summary>
///     The minutes the "Play for" and sleep timer dialogs accept.
/// </summary>
public static class ListeningMinutes
{
    public const int Default = 30;

    public static bool IsValid(double minutes) =>
        minutes is >= 1 and <= PlaybackMixLimits.MaxMinutes && minutes == Math.Truncate(minutes);
}

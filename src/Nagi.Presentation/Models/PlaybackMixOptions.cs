using Nagi.Core.Models;

namespace Nagi.Presentation.Models;

/// <summary>
///     The choices of the "Play for" dialog, with the input rules every UI head's dialog applies.
/// </summary>
public sealed record PlaybackMixOptions(int Minutes, ShuffleMode Mode, int AvoidRecentDays)
{
    public static bool IsValidAvoidRecentDays(double days) =>
        days is >= 0 and <= PlaybackMixLimits.MaxAvoidRecentDays && days == Math.Truncate(days);

    /// <summary>Only <see cref="ShuffleMode.MoreVariety" /> lets the user choose how many recent days to avoid.</summary>
    public static bool UsesAvoidRecentDays(ShuffleMode mode) => mode == ShuffleMode.MoreVariety;
}

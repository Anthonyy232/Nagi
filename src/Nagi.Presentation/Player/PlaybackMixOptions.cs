using Nagi.Core.Playback;

namespace Nagi.Presentation.Player;

/// <summary>
///     The choices of the "Play for" dialog.
/// </summary>
public sealed record PlaybackMixOptions(int Minutes, ShuffleMode Mode, int AvoidRecentDays);

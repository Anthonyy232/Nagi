using Nagi.Core.Models;

namespace Nagi.Presentation.Models;

/// <summary>
///     The choices of the "Play for" dialog.
/// </summary>
public sealed record PlaybackMixOptions(int Minutes, ShuffleMode Mode, int AvoidRecentDays);

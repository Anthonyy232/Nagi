using Nagi.Core.Models;

namespace Nagi.Presentation.Models;

public sealed record PlaybackMixOptions(int Minutes, ShuffleMode Mode, int AvoidRecentDays);

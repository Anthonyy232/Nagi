using Nagi.Core.Models;

namespace Nagi.WinUI.Models;

public sealed record PlaybackMixOptions(int Minutes, ShuffleMode Mode, int AvoidRecentDays);

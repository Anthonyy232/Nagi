using Nagi.Core.Playback;
using Nagi.Presentation.Common;

namespace Nagi.Presentation.Player;

/// <summary>
///     The "Play for" dialog: how long to play and which kind of mix.
/// </summary>
public sealed class PlayForDialogViewModel
{
    public PlayForDialogViewModel()
    {
        SelectedMode = ModeOptions[0];
    }

    public IReadOnlyList<SettingOption<ShuffleMode>> ModeOptions { get; } =
        SettingOption<ShuffleMode>.For(Enum.GetValues<ShuffleMode>(), ShuffleModeText.GetName);

    public double Minutes { get; set; } = ListeningMinutes.Default;

    public SettingOption<ShuffleMode> SelectedMode { get; set; }

    public double AvoidRecentDays { get; set; } = PlaybackLimits.DefaultAvoidRecentDays;

    /// <summary>Only <see cref="ShuffleMode.MoreVariety" /> lets the user choose how many recent days to avoid.</summary>
    public bool ShowsAvoidRecentDays => SelectedMode.Value == ShuffleMode.MoreVariety;

    public string ModeDescription => ShuffleModeText.GetDescription(SelectedMode.Value);

    public bool CanAccept => ListeningMinutes.IsValid(Minutes) && (!ShowsAvoidRecentDays ||
        AvoidRecentDays is >= 0 and <= PlaybackLimits.MaxAvoidRecentDays && AvoidRecentDays == Math.Truncate(AvoidRecentDays));

    public PlaybackMixOptions ToOptions() => new((int)Minutes, SelectedMode.Value,
        ShowsAvoidRecentDays ? (int)AvoidRecentDays : PlaybackLimits.DefaultAvoidRecentDays);
}

using System.Linq.Expressions;
using Nagi.Core.Library;
using Nagi.Core.Playback;

namespace Nagi.Core.Listening;

/// <summary>
///     When a listen counts as a play or a skip. <see cref="Song.PlayCount" /> counts eligible listens only, while
///     Insights also counts listens that played to the end, so a short song played through counts there too.
/// </summary>
public static class ListenRules
{
    /// <summary>
    ///     Whether a listen has played long enough to be eligible for scrobbling: the track is longer than 30 s and has
    ///     played half its length or 4 minutes. It applies whether or not a scrobbling account is set up.
    /// </summary>
    public static bool IsEligible(TimeSpan duration, TimeSpan position) =>
        duration.TotalSeconds > 30 && (position.TotalSeconds >= duration.TotalSeconds / 2 || position.TotalMinutes >= 4);

    /// <summary>Whether Insights counts a listen as a play.</summary>
    public static Expression<Func<ListenHistory, bool>> CountsAsPlay { get; } =
        listen => listen.IsEligibleForScrobbling || listen.EndReason == PlaybackEndReason.Finished;

    /// <summary>Whether a listen counts towards <see cref="Song.SkipCount" />: skipped before it became eligible.</summary>
    public static bool CountsAsSkip(ListenHistory listen) =>
        listen.EndReason == PlaybackEndReason.Skipped && !listen.IsEligibleForScrobbling;
}

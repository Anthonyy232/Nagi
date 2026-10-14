using System;

namespace Nagi.Presentation.Helpers;

public static class DurationFormatter
{
    /// <summary>
    ///     Formats as <c>m:ss</c>, <c>h:mm:ss</c> from one hour, or <c>Xh Ym</c> from 100 hours.
    /// </summary>
    public static string Format(TimeSpan timeSpan)
    {
        // Seconds are noise at this scale.
        if (timeSpan.TotalHours >= 100)
            return $"{(int)timeSpan.TotalHours}h {timeSpan.Minutes}m";

        // TotalHours, not the 'h' specifier, which wraps at 24 hours.
        if (timeSpan.TotalHours >= 1)
            return $"{(int)timeSpan.TotalHours}:{timeSpan.Minutes:D2}:{timeSpan.Seconds:D2}";

        return timeSpan.ToString(@"m\:ss");
    }
}

using CommunityToolkit.Mvvm.ComponentModel;

namespace Nagi.Core.Models.Lyrics;

/// <summary>
///     Represents a single line of a lyric file, including its text and timing information.
/// </summary>
public partial class LyricLine : ObservableObject
{
    /// <summary>
    ///     Gets or sets a value indicating whether this is the currently active lyric line.
    /// </summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>
    ///     Gets or sets the opacity of the lyric line, calculated based on its distance from the active line.
    /// </summary>
    [ObservableProperty] private double _opacity = 1.0;

    public LyricLine()
    {
    }

    public LyricLine(TimeSpan startTime, string text)
    {
        StartTime = startTime;
        Text = text;
    }

    /// <summary>
    ///     The timestamp at which this lyric line should be displayed.
    /// </summary>
    public TimeSpan StartTime { get; set; }

    /// <summary>
    ///     The text content of the lyric line.
    /// </summary>
    public string Text { get; set; } = string.Empty;
}
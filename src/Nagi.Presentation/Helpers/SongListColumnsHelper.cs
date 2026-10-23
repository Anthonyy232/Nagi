using System;
using System.Collections.Generic;
using Nagi.Core.Models;
using Nagi.Presentation.Models;
using Nagi.Presentation.Resources;
using CoreStrings = Nagi.Core.Resources.Strings;

namespace Nagi.Presentation.Helpers;

public static class SongListColumnsHelper
{
    /// <summary>
    ///     Every column, in the order a song row shows them.
    /// </summary>
    public static IReadOnlyList<SongListColumns> All { get; } =
    [
        SongListColumns.TrackNumber, SongListColumns.Title, SongListColumns.Artist, SongListColumns.Album,
        SongListColumns.Year, SongListColumns.Bpm, SongListColumns.Bitrate, SongListColumns.PlayCount,
        SongListColumns.LastPlayed, SongListColumns.DateAdded, SongListColumns.FileCreatedDate, SongListColumns.Duration
    ];

    public static string GetLabel(SongListColumns column) => column switch
    {
        SongListColumns.TrackNumber => CoreStrings.Label_TrackNumber,
        SongListColumns.Title => CoreStrings.Label_Title,
        SongListColumns.Artist => CoreStrings.Label_Artist,
        SongListColumns.Album => CoreStrings.Label_Album,
        SongListColumns.Year => CoreStrings.Label_Year,
        SongListColumns.Bpm => CoreStrings.Label_Bpm,
        SongListColumns.Bitrate => Strings.Label_Bitrate,
        SongListColumns.PlayCount => CoreStrings.Label_PlayCount,
        SongListColumns.LastPlayed => CoreStrings.Label_LastPlayed,
        SongListColumns.DateAdded => CoreStrings.Label_DateAdded,
        SongListColumns.FileCreatedDate => CoreStrings.Label_DateCreated,
        SongListColumns.Duration => CoreStrings.Label_Duration,
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, null)
    };

    /// <summary>
    ///     A song's text in one of the plain text columns, or empty when it has no value. Title, artist and album
    ///     aren't plain text: the UI lays them out itself.
    /// </summary>
    public static string GetText(SongListColumns column, Song song, bool showDiscNumber) => column switch
    {
        SongListColumns.TrackNumber => showDiscNumber ? song.TrackDisplay : song.TrackNumber?.ToString(),
        SongListColumns.Year => song.Album?.Year?.ToString(),
        SongListColumns.Bpm => song.Bpm?.ToString("0"),
        SongListColumns.Bitrate => song.Bitrate?.ToString("N0"),
        SongListColumns.PlayCount => song.PlayCount.ToString("N0"),
        SongListColumns.LastPlayed => FormatDate(song.LastPlayedDate),
        SongListColumns.DateAdded => FormatDate(song.DateAddedToLibrary),
        SongListColumns.FileCreatedDate => FormatDate(song.FileCreatedDate),
        SongListColumns.Duration => DurationFormatter.Format(song.Duration),
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, null)
    } ?? string.Empty;

    // Dates are stored in UTC.
    private static string? FormatDate(DateTime? utc) => utc?.ToLocalTime().ToString("d");
}

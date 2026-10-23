using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Helpers;
using Nagi.Presentation.Models;
using Nagi.Presentation.Resources;
using CoreStrings = Nagi.Core.Resources.Strings;

namespace Nagi.Presentation.ViewModels;

/// <summary>
///     A read-only view of a song's tags, file details and listening stats. Loaded once before it is shown.
/// </summary>
public sealed class SongInfoViewModel(
    ILibraryReader libraryReader,
    IFileSystemService fileSystem,
    ILogger<SongInfoViewModel> logger)
{
    public string Title { get; private set; } = string.Empty;
    public string ArtistName { get; private set; } = string.Empty;
    public string? ArtworkUri { get; private set; }
    public IReadOnlyList<SongInfoSection> Sections { get; private set; } = [];

    public async Task LoadAsync(Song song)
    {
        // List pages load songs without genres, comments and other heavy fields.
        try
        {
            song = await libraryReader.GetSongByIdAsync(song.Id).ConfigureAwait(false) ?? song;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load full details for song {SongId}", song.Id);
        }

        Title = song.Title;
        ArtistName = song.ArtistName;
        ArtworkUri = ImageUriHelper.GetUriWithCacheBuster(song.AlbumArtUriFromTrack, song.FileModifiedDate);
        Sections =
        [
            Section(Strings.SongInfo_Section_Tags,
                (CoreStrings.Label_Title, song.Title),
                (CoreStrings.Label_Artist, song.ArtistName),
                (CoreStrings.Label_Album, song.Album?.Title),
                (Strings.Label_AlbumArtist, song.Album?.ArtistName),
                (CoreStrings.Label_Genre, string.Join(", ", song.Genres.Select(g => g.Name).Order())),
                (CoreStrings.Label_Year, song.Year?.ToString()),
                (CoreStrings.Label_TrackNumber, NumberOfTotal(song.TrackNumber, song.TrackCount)),
                (Strings.Label_DiscNumber, NumberOfTotal(song.DiscNumber, song.DiscCount)),
                (Strings.Label_Composer, song.Composer),
                (Strings.Label_Conductor, song.Conductor),
                (Strings.Label_Grouping, song.Grouping),
                (CoreStrings.Label_Bpm, song.Bpm?.ToString("0.#")),
                (Strings.Label_ReplayGain, Format(Strings.SongInfo_Gain_Format, song.ReplayGainTrackGain)),
                (Strings.Label_Comment, song.Comment),
                (Strings.Label_Copyright, song.Copyright)),
            Section(CoreStrings.Label_File,
                (Strings.Label_Path, song.FilePath),
                (Strings.Label_Format, Path.GetExtension(song.FilePath).TrimStart('.').ToUpperInvariant()),
                (CoreStrings.Label_Duration, DurationFormatter.Format(song.Duration)),
                (Strings.Label_Bitrate, Format(Strings.SongInfo_Bitrate_Format, song.Bitrate)),
                (Strings.Label_SampleRate, Format(Strings.SongInfo_SampleRate_Format, song.SampleRate)),
                (Strings.Label_Channels, song.Channels?.ToString()),
                (Strings.Label_FileSize, Format(Strings.SongInfo_FileSize_Format, GetFileSizeInMegabytes(song.FilePath))),
                (CoreStrings.Label_DateCreated, FormatDate(song.FileCreatedDate)),
                (CoreStrings.Label_DateModified, FormatDate(song.FileModifiedDate))),
            Section(Strings.SongInfo_Section_Listening,
                (CoreStrings.Label_DateAdded, FormatDate(song.DateAddedToLibrary)),
                (CoreStrings.Label_PlayCount, song.PlayCount.ToString("N0")),
                (Strings.Label_SkipCount, song.SkipCount.ToString("N0")),
                (CoreStrings.Label_LastPlayed, FormatDate(song.LastPlayedDate)))
        ];
    }

    private static SongInfoSection Section(string title, params (string Label, string? Value)[] fields) =>
        new(title, fields.Where(f => !string.IsNullOrWhiteSpace(f.Value))
            .Select(f => new SongInfoField(f.Label, f.Value!)).ToList());

    private static string? NumberOfTotal(int? number, int? total) => number switch
    {
        null or 0 => null,
        _ when total > 0 => ResourceFormatter.Format(Strings.SongInfo_NumberOfTotal_Format, number, total),
        _ => number.ToString()
    };

    private static string? Format(string pattern, double? value) =>
        value is { } v ? ResourceFormatter.Format(pattern, v) : null;

    // Dates are stored in UTC.
    private static string? FormatDate(DateTime? utc) => utc?.ToLocalTime().ToString("g");

    private double? GetFileSizeInMegabytes(string path)
    {
        var file = fileSystem.GetFileInfo(path);
        return file.Exists ? file.Length / 1024d / 1024d : null;
    }
}

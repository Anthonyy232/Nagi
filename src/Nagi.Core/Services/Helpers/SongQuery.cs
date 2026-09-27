using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Helpers;

internal static class SongQuery
{
    public static IOrderedQueryable<Song> Sort(IQueryable<Song> query, SongSortOrder sortOrder, bool ignoreArticles = false)
    {
        Expression<Func<Song, string>> title = ignoreArticles ? s => s.SortTitle : s => s.Title;
        Expression<Func<Song, string>> artist = ignoreArticles ? s => s.PrimaryArtistSortName : s => s.PrimaryArtistName;
        Expression<Func<Song, string>> album = ignoreArticles
            ? s => s.Album != null ? s.Album.SortTitle : string.Empty
            : s => s.Album != null ? s.Album.Title : string.Empty;

        return sortOrder switch
        {
            SongSortOrder.TitleDesc => query.OrderByDescending(title).ThenByDescending(artist).ThenByDescending(s => s.Id),
            SongSortOrder.AlbumAsc or SongSortOrder.TrackNumberAsc => ByTracks(query.OrderBy(album)),
            SongSortOrder.AlbumDesc or SongSortOrder.TrackNumberDesc => ByTracksDescending(query.OrderByDescending(album)),
            SongSortOrder.ArtistAsc => ByTracks(query.OrderBy(artist).ThenBy(album)),
            SongSortOrder.ArtistDesc => ByTracksDescending(query.OrderByDescending(artist).ThenByDescending(album)),
            SongSortOrder.YearAsc => ByTracks(query.OrderBy(s => s.Year).ThenBy(artist).ThenBy(album)),
            SongSortOrder.YearDesc => ByTracksDescending(query.OrderByDescending(s => s.Year).ThenByDescending(artist).ThenByDescending(album)),
            SongSortOrder.FileCreatedDateAsc => ByTracks(query.OrderBy(s => s.FileCreatedDate).ThenBy(artist).ThenBy(album)),
            SongSortOrder.FileCreatedDateDesc => ByTracksDescending(query.OrderByDescending(s => s.FileCreatedDate).ThenByDescending(artist).ThenByDescending(album)),
            SongSortOrder.PlayCountAsc => ByMetric(s => s.PlayCount),
            SongSortOrder.PlayCountDesc => ByMetricDescending(s => s.PlayCount),
            SongSortOrder.LastPlayedAsc => ByMetric(s => s.LastPlayedDate),
            SongSortOrder.LastPlayedDesc => ByMetricDescending(s => s.LastPlayedDate),
            SongSortOrder.DateAddedAsc => ByMetric(s => s.DateAddedToLibrary),
            SongSortOrder.DateAddedDesc => ByMetricDescending(s => s.DateAddedToLibrary),
            SongSortOrder.DurationAsc => ByMetric(s => s.DurationTicks),
            SongSortOrder.DurationDesc => ByMetricDescending(s => s.DurationTicks),
            SongSortOrder.BpmAsc => ByMetric(s => s.Bpm ?? 0),
            SongSortOrder.BpmDesc => ByMetricDescending(s => s.Bpm ?? 0),
            SongSortOrder.Random => query.OrderBy(_ => EF.Functions.Random()),
            _ => query.OrderBy(title).ThenBy(artist).ThenBy(s => s.Id)
        };

        IOrderedQueryable<Song> ByTracks(IOrderedQueryable<Song> ordered) =>
            ordered.ThenBy(s => s.DiscNumber ?? 0).ThenBy(s => s.TrackNumber).ThenBy(title).ThenBy(s => s.Id);

        IOrderedQueryable<Song> ByTracksDescending(IOrderedQueryable<Song> ordered) =>
            ordered.ThenByDescending(s => s.DiscNumber ?? 0).ThenByDescending(s => s.TrackNumber)
                .ThenByDescending(title).ThenByDescending(s => s.Id);

        IOrderedQueryable<Song> ByMetric<TKey>(Expression<Func<Song, TKey>> key) =>
            query.OrderBy(key).ThenBy(artist).ThenBy(title).ThenBy(s => s.Id);

        IOrderedQueryable<Song> ByMetricDescending<TKey>(Expression<Func<Song, TKey>> key) =>
            query.OrderByDescending(key).ThenByDescending(artist).ThenByDescending(title).ThenByDescending(s => s.Id);
    }

    // List rows omit lyrics and collection navigations; fetch full entities for playback or editing.
    public static IQueryable<Song> ExcludeHeavyFields(IQueryable<Song> query)
    {
        return query.Select(s => new Song
        {
            Id = s.Id,
            Title = s.Title,
            AlbumId = s.AlbumId,
            Album = s.Album,
            Composer = s.Composer,
            FolderId = s.FolderId,
            Folder = s.Folder,
            DurationTicks = s.DurationTicks,
            AlbumArtUriFromTrack = s.AlbumArtUriFromTrack,
            FilePath = s.FilePath,
            DirectoryPath = s.DirectoryPath,
            Year = s.Year,
            TrackNumber = s.TrackNumber,
            TrackCount = s.TrackCount,
            DiscNumber = s.DiscNumber,
            DiscCount = s.DiscCount,
            SampleRate = s.SampleRate,
            Bitrate = s.Bitrate,
            Channels = s.Channels,
            DateAddedToLibrary = s.DateAddedToLibrary,
            FileCreatedDate = s.FileCreatedDate,
            FileModifiedDate = s.FileModifiedDate,
            LightSwatchId = s.LightSwatchId,
            DarkSwatchId = s.DarkSwatchId,
            Rating = s.Rating,
            IsLoved = s.IsLoved,
            PlayCount = s.PlayCount,
            SkipCount = s.SkipCount,
            LastPlayedDate = s.LastPlayedDate,
            LrcFilePath = s.LrcFilePath,
            LyricsLastCheckedUtc = s.LyricsLastCheckedUtc,
            Bpm = s.Bpm,
            ReplayGainTrackGain = s.ReplayGainTrackGain,
            ReplayGainTrackPeak = s.ReplayGainTrackPeak,
            Grouping = s.Grouping,
            Conductor = s.Conductor,
            MusicBrainzTrackId = s.MusicBrainzTrackId,
            MusicBrainzReleaseId = s.MusicBrainzReleaseId,
            ArtistName = s.ArtistName,
            PrimaryArtistName = s.PrimaryArtistName,
            SortTitle = s.SortTitle,
            PrimaryArtistSortName = s.PrimaryArtistSortName
        });
    }
}

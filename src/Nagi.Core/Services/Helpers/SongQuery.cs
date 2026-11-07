using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Nagi.Core.Data;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Helpers;

internal static class SongQuery
{
    /// <summary>
    ///     The <paramref name="songs" /> whose title, artists, album, year or genres contain the search term, or all of
    ///     them for a blank one. Every song list searches this way.
    /// </summary>
    public static IQueryable<Song> Matching(MusicDbContext context, IQueryable<Song> songs, string? searchTerm)
    {
        if (ArtistNameHelper.NormalizeStringCore(searchTerm) is not { } normalizedTerm) return songs;

        var term = LikePatternHelper.CreateContainsPattern(normalizedTerm);
        var artistIds = context.Artists
            .Where(artist => EF.Functions.Like(artist.Name, term, LikePatternHelper.EscapeCharacter))
            .Select(artist => artist.Id);
        var songArtistIds = context.SongArtists.Where(sa => artistIds.Contains(sa.ArtistId)).Select(sa => sa.SongId);
        var albumIds = context.Albums
            .Where(album => EF.Functions.Like(album.Title, term, LikePatternHelper.EscapeCharacter)
                            || EF.Functions.Like(album.ArtistName, term, LikePatternHelper.EscapeCharacter))
            .Select(album => (Guid?)album.Id)
            .Union(context.AlbumArtists.Where(aa => artistIds.Contains(aa.ArtistId)).Select(aa => (Guid?)aa.AlbumId));
        var genreSongIds = context.Genres
            .Where(genre => EF.Functions.Like(genre.Name, term, LikePatternHelper.EscapeCharacter))
            .SelectMany(genre => genre.Songs).Select(song => song.Id);

        return songs
            .Where(s =>
                EF.Functions.Like(s.Title, term, LikePatternHelper.EscapeCharacter)
                || EF.Functions.Like(s.ArtistName, term, LikePatternHelper.EscapeCharacter)
                || songArtistIds.Contains(s.Id)
                || albumIds.Contains(s.AlbumId)
                || (s.Year != null && EF.Functions.Like(s.Year.ToString(), term, LikePatternHelper.EscapeCharacter))
                || genreSongIds.Contains(s.Id)
            );
    }

    public static IOrderedQueryable<Song> Sort(IQueryable<Song> query, SongSortOrder sortOrder, bool ignoreArticles)
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
            RemoteId = s.RemoteId,
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
            MusicBrainzReleaseGroupId = s.MusicBrainzReleaseGroupId,
            ArtistName = s.ArtistName,
            PrimaryArtistName = s.PrimaryArtistName,
            SortTitle = s.SortTitle,
            PrimaryArtistSortName = s.PrimaryArtistSortName
        });
    }
}

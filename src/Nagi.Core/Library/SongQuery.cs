using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Nagi.Core.Data;
using Nagi.Core.Platform;

namespace Nagi.Core.Library;

internal static class SongQuery
{
    /// <summary>
    ///     The <paramref name="songs" /> whose title, artists, album, year or genres contain the search term, or all of
    ///     them for a blank one. Every song list searches this way.
    /// </summary>
    public static IQueryable<Song> Matching(MusicDbContext context, IQueryable<Song> songs, string? searchTerm)
    {
        if (TextNormalizer.Normalize(searchTerm) is not { } normalizedTerm) return songs;

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

    /// <param name="playlistId">
    ///     The playlist whose order <see cref="SongSortOrder.PlaylistOrder" /> follows; without one, that order sorts by
    ///     title.
    /// </param>
    /// <param name="sortTag">
    ///     The <see cref="SongTags.NormalizeName">normalized</see> name of the tag <see cref="SongSortOrder.TagAsc" />
    ///     and <see cref="SongSortOrder.TagDesc" /> sort by; without one, those orders sort by title.
    /// </param>
    public static IOrderedQueryable<Song> Sort(IQueryable<Song> query, SongSortOrder sortOrder, bool ignoreArticles,
        Guid? playlistId = null, string? sortTag = null)
    {
        var tagPath = sortTag is null ? null : SongTags.PathOf(sortTag);
        // Numbers in number order, then text; songs without the tag come first.
        Expression<Func<Song, double?>> tagNumber = s => MusicDbContext.ToNumber(MusicDbContext.JsonExtract(s.Tags, tagPath!));
        Expression<Func<Song, string?>> tagText = s => EF.Functions.Collate(MusicDbContext.JsonExtract(s.Tags, tagPath!), "NOCASE");

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
            SongSortOrder.FileModifiedDateAsc => ByTracks(query.OrderBy(s => s.FileModifiedDate).ThenBy(artist).ThenBy(album)),
            SongSortOrder.FileModifiedDateDesc => ByTracksDescending(query.OrderByDescending(s => s.FileModifiedDate).ThenByDescending(artist).ThenByDescending(album)),
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
            SongSortOrder.TagAsc when tagPath is not null =>
                query.OrderBy(tagNumber).ThenBy(tagText).ThenBy(artist).ThenBy(title).ThenBy(s => s.Id),
            SongSortOrder.TagDesc when tagPath is not null =>
                query.OrderByDescending(tagNumber).ThenByDescending(tagText).ThenByDescending(artist).ThenByDescending(title).ThenByDescending(s => s.Id),
            SongSortOrder.Random => query.OrderBy(_ => EF.Functions.Random()),
            SongSortOrder.PlaylistOrder when playlistId is { } id => query
                .OrderBy(s => s.PlaylistSongs.Where(ps => ps.PlaylistId == id).Select(ps => ps.Order).FirstOrDefault())
                .ThenBy(s => s.Id),
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

    // Long free-text tags that list rows leave out; playback and editing read the full song.
    private static readonly string[] HeavyFields = [nameof(Song.Lyrics), nameof(Song.Comment), nameof(Song.Tags)];

    private static Expression<Func<Song, Song>>? _listRow;

    /// <summary>
    ///     Songs as list rows: every mapped column except <see cref="HeavyFields" />, with the album and folder but
    ///     without collection navigations.
    /// </summary>
    public static IQueryable<Song> ExcludeHeavyFields(MusicDbContext context, IQueryable<Song> query) =>
        query.Select(_listRow ??= BuildListRow(context));

    // Built from the model, so a new column reaches list rows without being listed here.
    private static Expression<Func<Song, Song>> BuildListRow(MusicDbContext context)
    {
        var song = Expression.Parameter(typeof(Song), "s");
        var columns = context.Model.FindEntityType(typeof(Song))!.GetProperties()
            .Select(property => property.PropertyInfo)
            .OfType<PropertyInfo>()
            .Where(property => !HeavyFields.Contains(property.Name));
        var bindings = columns
            .Concat([typeof(Song).GetProperty(nameof(Song.Album))!, typeof(Song).GetProperty(nameof(Song.Folder))!])
            .Select(property => Expression.Bind(property, Expression.Property(song, property)));
        return Expression.Lambda<Func<Song, Song>>(Expression.MemberInit(Expression.New(typeof(Song)), bindings), song);
    }
}

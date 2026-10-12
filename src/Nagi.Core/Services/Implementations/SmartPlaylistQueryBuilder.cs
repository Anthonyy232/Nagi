using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Nagi.Core.Data;
using Nagi.Core.Models;
using Nagi.Core.Helpers;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Helpers;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Builds dynamic EF Core queries from smart playlist rules.
///     This is the core rule engine that translates user-defined conditions into LINQ expressions.
/// </summary>
public class SmartPlaylistQueryBuilder
{
    public IQueryable<Song> BuildQuery(MusicDbContext context, SmartPlaylist smartPlaylist, string? searchTerm = null, SongSortOrder? sortOrderOverride = null)
    {
        var query = BuildCountQuery(context, smartPlaylist, searchTerm)
            .Include(s => s.Album)
            .Include(s => s.Genres)
            .AsSplitQuery();

        return SongQuery.Sort(query, sortOrderOverride ?? SortOrderHelper.MapToSongSortOrder(smartPlaylist.SortOrder));
    }

    /// <summary>
    ///     Builds a count-only query (more efficient than BuildQuery for counting).
    /// </summary>
    public IQueryable<Song> BuildCountQuery(MusicDbContext context, SmartPlaylist smartPlaylist, string? searchTerm = null)
    {
        var query = context.Songs.AsNoTracking();

        query = ApplyRuleFilters(query, smartPlaylist);

        if (!string.IsNullOrWhiteSpace(searchTerm))
            query = ApplySearchFilter(query, searchTerm);

        return query;
    }

    private static IQueryable<Song> ApplyRuleFilters(IQueryable<Song> query, SmartPlaylist smartPlaylist)
    {
        if (smartPlaylist.MatchAllRules)
        {
            // AND logic: chain Where clauses
            foreach (var rule in smartPlaylist.Rules)
            {
                var predicate = BuildRulePredicate(rule);
                if (predicate != null)
                    query = query.Where(predicate);
            }
        }
        else
        {
            // OR logic: combine predicates
            var predicates = smartPlaylist.Rules
                .Select(BuildRulePredicate)
                .OfType<Expression<Func<Song, bool>>>()
                .ToList();

            if (predicates.Count > 0)
            {
                var combined = predicates.Aggregate(CombineWithOr);
                query = query.Where(combined);
            }
        }

        return query;
    }

    private static Expression<Func<Song, bool>>? BuildRulePredicate(SmartPlaylistRule rule)
    {
        return rule.Field switch
        {
            // Text fields
            SmartPlaylistField.Title => BuildTextPredicate(s => s.Title, rule),
            SmartPlaylistField.Artist => BuildArtistPredicate(rule),
            SmartPlaylistField.Album => BuildTextPredicate(s => s.Album != null ? s.Album.Title : null, rule),
            SmartPlaylistField.Composer => BuildTextPredicate(s => s.Composer, rule),
            SmartPlaylistField.Comment => BuildTextPredicate(s => s.Comment, rule),
            SmartPlaylistField.Grouping => BuildTextPredicate(s => s.Grouping, rule),

            // Genre is special (many-to-many)
            SmartPlaylistField.Genre => BuildGenrePredicate(rule),

            // Numeric fields
            SmartPlaylistField.PlayCount => BuildNumericPredicate(s => s.PlayCount, rule),
            SmartPlaylistField.SkipCount => BuildNumericPredicate(s => s.SkipCount, rule),
            SmartPlaylistField.Rating => BuildNullableNumericPredicate(s => s.Rating, rule),
            SmartPlaylistField.Year => BuildNullableNumericPredicate(s => s.Year, rule),
            SmartPlaylistField.TrackNumber => BuildNullableNumericPredicate(s => s.TrackNumber, rule),
            SmartPlaylistField.DiscNumber => BuildNullableNumericPredicate(s => s.DiscNumber, rule),
            SmartPlaylistField.Bpm => BuildNullableDoublePredicate(s => s.Bpm, rule),
            SmartPlaylistField.Duration => BuildDurationPredicate(rule),
            SmartPlaylistField.Bitrate => BuildNullableNumericPredicate(s => s.Bitrate, rule),
            SmartPlaylistField.SampleRate => BuildNullableNumericPredicate(s => s.SampleRate, rule),

            // Boolean fields
            SmartPlaylistField.IsLoved => BuildBooleanPredicate(s => s.IsLoved, rule),
            SmartPlaylistField.HasLyrics => BuildBooleanPredicate(s => !string.IsNullOrEmpty(s.LrcFilePath) || !string.IsNullOrEmpty(s.Lyrics), rule),

            // Date fields
            SmartPlaylistField.DateAdded => BuildDatePredicate(s => s.DateAddedToLibrary, rule),
            SmartPlaylistField.LastPlayed => BuildDatePredicate(s => s.LastPlayedDate, rule),
            SmartPlaylistField.FileCreatedDate => BuildDatePredicate(s => s.FileCreatedDate, rule),
            SmartPlaylistField.FileModifiedDate => BuildDatePredicate(s => s.FileModifiedDate, rule),

            _ => null
        };
    }

    private static Expression<Func<Song, bool>>? BuildTextPredicate(
        Expression<Func<Song, string?>> selector, SmartPlaylistRule rule)
    {
        var value = (rule.Value ?? string.Empty).ToLowerInvariant();

        // Use ToLower() for EF Core SQL translation compatibility instead of StringComparison
        return rule.Operator switch
        {
            SmartPlaylistOperator.Is =>
                CombineSelector(selector, v => v != null && v.ToLower() == value),
            SmartPlaylistOperator.IsNot =>
                CombineSelector(selector, v => v == null || v.ToLower() != value),
            SmartPlaylistOperator.Contains =>
                CombineSelector(selector, v => v != null && v.ToLower().Contains(value)),
            SmartPlaylistOperator.DoesNotContain =>
                CombineSelector(selector, v => v == null || !v.ToLower().Contains(value)),
            SmartPlaylistOperator.StartsWith =>
                CombineSelector(selector, v => v != null && v.ToLower().StartsWith(value)),
            SmartPlaylistOperator.EndsWith =>
                CombineSelector(selector, v => v != null && v.ToLower().EndsWith(value)),
            _ => null
        };
    }

    private static Expression<Func<Song, bool>>? BuildGenrePredicate(SmartPlaylistRule rule)
    {
        var value = (rule.Value ?? string.Empty).ToLowerInvariant();

        return rule.Operator switch
        {
            SmartPlaylistOperator.Is =>
                s => s.Genres.Any(g => g.Name.ToLower() == value),
            SmartPlaylistOperator.IsNot =>
                s => !s.Genres.Any(g => g.Name.ToLower() == value),
            SmartPlaylistOperator.Contains =>
                s => s.Genres.Any(g => g.Name.ToLower().Contains(value)),
            SmartPlaylistOperator.DoesNotContain =>
                s => !s.Genres.Any(g => g.Name.ToLower().Contains(value)),
            SmartPlaylistOperator.StartsWith =>
                s => s.Genres.Any(g => g.Name.ToLower().StartsWith(value)),
            SmartPlaylistOperator.EndsWith =>
                s => s.Genres.Any(g => g.Name.ToLower().EndsWith(value)),
            _ => null
        };
    }

    private static Expression<Func<Song, bool>>? BuildArtistPredicate(SmartPlaylistRule rule)
    {
        var value = (rule.Value ?? string.Empty).ToLowerInvariant();

        // Use ANY to check against all artists associated with the song.
        // This ensures that "Artist IS 'Daft Punk'" matches "Daft Punk & Julian Casablancas"
        return rule.Operator switch
        {
            SmartPlaylistOperator.Is =>
                s => s.SongArtists.Any(sa => sa.Artist.Name.ToLower() == value),
            SmartPlaylistOperator.IsNot =>
                s => !s.SongArtists.Any(sa => sa.Artist.Name.ToLower() == value),
            SmartPlaylistOperator.Contains =>
                s => s.SongArtists.Any(sa => sa.Artist.Name.ToLower().Contains(value)),
            SmartPlaylistOperator.DoesNotContain =>
                s => !s.SongArtists.Any(sa => sa.Artist.Name.ToLower().Contains(value)),
            SmartPlaylistOperator.StartsWith =>
                s => s.SongArtists.Any(sa => sa.Artist.Name.ToLower().StartsWith(value)),
            SmartPlaylistOperator.EndsWith =>
                s => s.SongArtists.Any(sa => sa.Artist.Name.ToLower().EndsWith(value)),
            _ => null
        };
    }

    private static Expression<Func<Song, bool>>? BuildNumericPredicate(
        Expression<Func<Song, int>> selector, SmartPlaylistRule rule)
    {
        if (!int.TryParse(rule.Value, out var value)) return null;

        return rule.Operator switch
        {
            SmartPlaylistOperator.Equals => CombineSelector(selector, v => v == value),
            SmartPlaylistOperator.NotEquals => CombineSelector(selector, v => v != value),
            SmartPlaylistOperator.GreaterThan => CombineSelector(selector, v => v > value),
            SmartPlaylistOperator.LessThan => CombineSelector(selector, v => v < value),
            SmartPlaylistOperator.GreaterThanOrEqual => CombineSelector(selector, v => v >= value),
            SmartPlaylistOperator.LessThanOrEqual => CombineSelector(selector, v => v <= value),
            SmartPlaylistOperator.IsInRange when int.TryParse(rule.SecondValue, out var value2) =>
                CombineSelector(selector, v => v >= value && v <= value2),
            _ => null
        };
    }

    private static Expression<Func<Song, bool>>? BuildNullableNumericPredicate(
        Expression<Func<Song, int?>> selector, SmartPlaylistRule rule)
    {
        if (!int.TryParse(rule.Value, out var value)) return null;

        return rule.Operator switch
        {
            SmartPlaylistOperator.Equals => CombineSelector(selector, v => v.HasValue && v.Value == value),
            SmartPlaylistOperator.NotEquals => CombineSelector(selector, v => !v.HasValue || v.Value != value),
            SmartPlaylistOperator.GreaterThan => CombineSelector(selector, v => v.HasValue && v.Value > value),
            SmartPlaylistOperator.LessThan => CombineSelector(selector, v => v.HasValue && v.Value < value),
            SmartPlaylistOperator.GreaterThanOrEqual => CombineSelector(selector, v => v.HasValue && v.Value >= value),
            SmartPlaylistOperator.LessThanOrEqual => CombineSelector(selector, v => v.HasValue && v.Value <= value),
            SmartPlaylistOperator.IsInRange when int.TryParse(rule.SecondValue, out var value2) =>
                CombineSelector(selector, v => v.HasValue && v.Value >= value && v.Value <= value2),
            _ => null
        };
    }

    private static Expression<Func<Song, bool>>? BuildNullableDoublePredicate(
        Expression<Func<Song, double?>> selector, SmartPlaylistRule rule)
    {
        if (!SmartPlaylistRuleValue.TryParseNumber(rule.Value, out var value)) return null;

        // Use range check instead of Math.Abs for better SQL translation
        const double epsilon = 0.5; // BPM tolerance (e.g., 120 matches 119.5-120.5)

        return rule.Operator switch
        {
            SmartPlaylistOperator.Equals => CombineSelector(selector, v => v.HasValue && v.Value >= value - epsilon && v.Value <= value + epsilon),
            SmartPlaylistOperator.NotEquals => CombineSelector(selector, v => !v.HasValue || v.Value < value - epsilon || v.Value > value + epsilon),
            SmartPlaylistOperator.GreaterThan => CombineSelector(selector, v => v.HasValue && v.Value > value),
            SmartPlaylistOperator.LessThan => CombineSelector(selector, v => v.HasValue && v.Value < value),
            SmartPlaylistOperator.GreaterThanOrEqual => CombineSelector(selector, v => v.HasValue && v.Value >= value),
            SmartPlaylistOperator.LessThanOrEqual => CombineSelector(selector, v => v.HasValue && v.Value <= value),
            SmartPlaylistOperator.IsInRange when SmartPlaylistRuleValue.TryParseNumber(rule.SecondValue, out var value2) =>
                CombineSelector(selector, v => v.HasValue && v.Value >= value && v.Value <= value2),
            _ => null
        };
    }

    private static Expression<Func<Song, bool>>? BuildDurationPredicate(SmartPlaylistRule rule)
    {
        // Duration values are in seconds - convert to ticks for database comparison
        if (!TryParseDuration(rule.Value, out var durationTicks)) return null;

        return rule.Operator switch
        {
            SmartPlaylistOperator.Equals => s => s.DurationTicks == durationTicks,
            SmartPlaylistOperator.NotEquals => s => s.DurationTicks != durationTicks,
            SmartPlaylistOperator.GreaterThan => s => s.DurationTicks > durationTicks,
            SmartPlaylistOperator.LessThan => s => s.DurationTicks < durationTicks,
            SmartPlaylistOperator.GreaterThanOrEqual => s => s.DurationTicks >= durationTicks,
            SmartPlaylistOperator.LessThanOrEqual => s => s.DurationTicks <= durationTicks,
            SmartPlaylistOperator.IsInRange when TryParseDuration(rule.SecondValue, out var upperTicks) =>
                s => s.DurationTicks >= durationTicks && s.DurationTicks <= upperTicks,
            _ => null
        };
    }

    private static bool TryParseDuration(string? value, out long ticks)
    {
        ticks = 0;
        if (!SmartPlaylistRuleValue.TryParseNumber(value, out var seconds)) return false;
        try
        {
            ticks = TimeSpan.FromSeconds(seconds).Ticks;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static Expression<Func<Song, bool>>? BuildBooleanPredicate(
        Expression<Func<Song, bool>> selector, SmartPlaylistRule rule)
    {
        return rule.Operator switch
        {
            SmartPlaylistOperator.IsTrue => selector,
            SmartPlaylistOperator.IsFalse => CombineSelector(selector, v => !v),
            _ => null
        };
    }

    private static Expression<Func<Song, bool>>? BuildDatePredicate(
        Expression<Func<Song, DateTime?>> selector, SmartPlaylistRule rule)
    {
        if (rule.Operator is SmartPlaylistOperator.IsInTheLast or SmartPlaylistOperator.IsNotInTheLast)
        {
            if (!int.TryParse(rule.Value, out var days) || days < 0) return null;
            var now = DateTime.UtcNow;
            if (days > (now - DateTime.MinValue).TotalDays) return null;
            var threshold = now.AddDays(-days);
            return rule.Operator == SmartPlaylistOperator.IsInTheLast
                ? CombineSelector(selector, v => v.HasValue && v.Value >= threshold)
                : CombineSelector(selector, v => !v.HasValue || v.Value < threshold);
        }

        return rule.Operator switch
        {
            SmartPlaylistOperator.GreaterThan when SmartPlaylistRuleValue.TryParseDate(rule.Value, out var date) =>
                CombineSelector(selector, v => v.HasValue && v.Value > date),
            SmartPlaylistOperator.LessThan when SmartPlaylistRuleValue.TryParseDate(rule.Value, out var date) =>
                CombineSelector(selector, v => v.HasValue && v.Value < date),
            SmartPlaylistOperator.IsInRange when SmartPlaylistRuleValue.TryParseDate(rule.Value, out var date1) &&
                                                 SmartPlaylistRuleValue.TryParseDate(rule.SecondValue, out var date2) =>
                CombineSelector(selector, v => v.HasValue && v.Value >= date1 && v.Value <= date2),
            _ => null
        };
    }

    private static Expression<Func<Song, bool>> CombineSelector<T>(
        Expression<Func<Song, T>> selector,
        Expression<Func<T, bool>> predicate)
    {
        var body = ReplacingExpressionVisitor.Replace(predicate.Parameters[0], selector.Body, predicate.Body);
        return Expression.Lambda<Func<Song, bool>>(body, selector.Parameters);
    }

    private static Expression<Func<Song, bool>> CombineWithOr(
        Expression<Func<Song, bool>> left,
        Expression<Func<Song, bool>> right)
    {
        var rightBody = ReplacingExpressionVisitor.Replace(right.Parameters[0], left.Parameters[0], right.Body);
        return Expression.Lambda<Func<Song, bool>>(Expression.OrElse(left.Body, rightBody), left.Parameters);
    }

    private static IQueryable<Song> ApplySearchFilter(IQueryable<Song> query, string searchTerm)
    {
        var normalizedTerm = ArtistNameHelper.NormalizeStringCore(searchTerm) ?? searchTerm;
        var term = LikePatternHelper.CreateContainsPattern(normalizedTerm);
        return query.Where(s =>
            EF.Functions.Like(s.Title, term, LikePatternHelper.EscapeCharacter)
            || EF.Functions.Like(s.ArtistName, term, LikePatternHelper.EscapeCharacter)
            || s.SongArtists.Any(sa => EF.Functions.Like(sa.Artist.Name, term, LikePatternHelper.EscapeCharacter))
            || (s.Album != null && (EF.Functions.Like(s.Album.Title, term, LikePatternHelper.EscapeCharacter) || EF.Functions.Like(s.Album.ArtistName, term, LikePatternHelper.EscapeCharacter) || s.Album.AlbumArtists.Any(aa => EF.Functions.Like(aa.Artist.Name, term, LikePatternHelper.EscapeCharacter)))));
    }

}

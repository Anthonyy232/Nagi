using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Nagi.Core.Data;
using Nagi.Core.Models;
using Nagi.Core.Helpers;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Builds dynamic EF Core queries from smart playlist rules.
///     This is the core rule engine that translates user-defined conditions into LINQ expressions.
/// </summary>
public class SmartPlaylistQueryBuilder
{
    /// <summary>
    ///     Builds a queryable for songs matching the smart playlist's rules.
    /// </summary>
    public IQueryable<Song> BuildQuery(MusicDbContext context, SmartPlaylist smartPlaylist, string? searchTerm = null)
    {
        var query = context.Songs.AsNoTracking()
            .Include(s => s.Album)
            .Include(s => s.Genres)
            .AsSplitQuery();

        query = ApplyRuleFilters(query, smartPlaylist);

        if (!string.IsNullOrWhiteSpace(searchTerm))
            query = ApplySearchFilter(query, searchTerm);

        query = ApplySortOrder(query, smartPlaylist.SortOrder);

        return query;
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

    /// <summary>
    ///     Applies rule-based filtering to a query based on the smart playlist's rules and match logic.
    /// </summary>
    private static IQueryable<Song> ApplyRuleFilters(IQueryable<Song> query, SmartPlaylist smartPlaylist)
    {
        var orderedRules = smartPlaylist.Rules.OrderBy(r => r.Order).ToList();
        if (orderedRules.Count == 0)
            return query;

        if (smartPlaylist.MatchAllRules)
        {
            // AND logic: chain Where clauses
            foreach (var rule in orderedRules)
            {
                var predicate = BuildRulePredicate(rule);
                if (predicate != null)
                    query = query.Where(predicate);
            }
        }
        else
        {
            // OR logic: combine predicates
            var predicates = orderedRules
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
            SmartPlaylistField.HasLyrics => BuildBooleanPredicate(s => !string.IsNullOrEmpty(s.LrcFilePath), rule),

            // Date fields
            SmartPlaylistField.DateAdded => BuildDatePredicate(s => s.DateAddedToLibrary, rule),
            SmartPlaylistField.LastPlayed => BuildDatePredicate(s => s.LastPlayedDate, rule),
            SmartPlaylistField.FileCreatedDate => BuildDatePredicate(s => s.FileCreatedDate, rule),
            SmartPlaylistField.FileModifiedDate => BuildDatePredicate(s => s.FileModifiedDate, rule),

            _ => null
        };
    }

    #region Text Predicates

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

    #endregion

    #region Numeric Predicates

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
        if (!double.TryParse(rule.Value, out var value)) return null;

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
            SmartPlaylistOperator.IsInRange when double.TryParse(rule.SecondValue, out var value2) =>
                CombineSelector(selector, v => v.HasValue && v.Value >= value && v.Value <= value2),
            _ => null
        };
    }

    private static Expression<Func<Song, bool>>? BuildDurationPredicate(SmartPlaylistRule rule)
    {
        // Duration values are in seconds - convert to ticks for database comparison
        if (!double.TryParse(rule.Value, out var seconds)) return null;
        var durationTicks = (long)(seconds * TimeSpan.TicksPerSecond);

        return rule.Operator switch
        {
            SmartPlaylistOperator.Equals => s => s.DurationTicks == durationTicks,
            SmartPlaylistOperator.NotEquals => s => s.DurationTicks != durationTicks,
            SmartPlaylistOperator.GreaterThan => s => s.DurationTicks > durationTicks,
            SmartPlaylistOperator.LessThan => s => s.DurationTicks < durationTicks,
            SmartPlaylistOperator.GreaterThanOrEqual => s => s.DurationTicks >= durationTicks,
            SmartPlaylistOperator.LessThanOrEqual => s => s.DurationTicks <= durationTicks,
            SmartPlaylistOperator.IsInRange when double.TryParse(rule.SecondValue, out var seconds2) =>
                s => s.DurationTicks >= durationTicks && s.DurationTicks <= (long)(seconds2 * TimeSpan.TicksPerSecond),
            _ => null
        };
    }

    #endregion

    #region Boolean Predicates

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

    #endregion

    #region Date Predicates

    private static Expression<Func<Song, bool>>? BuildDatePredicate(
        Expression<Func<Song, DateTime?>> selector, SmartPlaylistRule rule)
    {
        // Capture threshold date as variable for proper EF Core translation
        if (rule.Operator == SmartPlaylistOperator.IsInTheLast && int.TryParse(rule.Value, out var days1))
        {
            var threshold = DateTime.UtcNow.AddDays(-days1);
            return CombineSelector(selector, v => v.HasValue && v.Value >= threshold);
        }

        if (rule.Operator == SmartPlaylistOperator.IsNotInTheLast && int.TryParse(rule.Value, out var days2))
        {
            var threshold = DateTime.UtcNow.AddDays(-days2);
            return CombineSelector(selector, v => !v.HasValue || v.Value < threshold);
        }

        return rule.Operator switch
        {
            SmartPlaylistOperator.GreaterThan when DateTime.TryParse(rule.Value, out var date) =>
                CombineSelector(selector, v => v.HasValue && v.Value > date),
            SmartPlaylistOperator.LessThan when DateTime.TryParse(rule.Value, out var date) =>
                CombineSelector(selector, v => v.HasValue && v.Value < date),
            SmartPlaylistOperator.IsInRange when DateTime.TryParse(rule.Value, out var date1) &&
                                                 DateTime.TryParse(rule.SecondValue, out var date2) =>
                CombineSelector(selector, v => v.HasValue && v.Value >= date1 && v.Value <= date2),
            _ => null
        };
    }

    #endregion

    #region Sorting

    private static IQueryable<Song> ApplySortOrder(IQueryable<Song> query, SmartPlaylistSortOrder sortOrder)
    {
        return sortOrder switch
        {
            SmartPlaylistSortOrder.TitleAsc => query.OrderBy(s => s.Title).ThenBy(s => s.Id),
            SmartPlaylistSortOrder.TitleDesc => query.OrderByDescending(s => s.Title).ThenByDescending(s => s.Id),
            SmartPlaylistSortOrder.ArtistAsc => query.OrderBy(s => s.PrimaryArtistName)
                .ThenBy(s => s.Album != null ? s.Album.Title : string.Empty)
                .ThenBy(s => s.DiscNumber ?? 0)
                .ThenBy(s => s.TrackNumber)
                .ThenBy(s => s.Title)
                .ThenBy(s => s.Id),
            SmartPlaylistSortOrder.ArtistDesc => query.OrderByDescending(s => s.PrimaryArtistName)
                .ThenByDescending(s => s.Album != null ? s.Album.Title : string.Empty)
                .ThenByDescending(s => s.DiscNumber ?? 0)
                .ThenByDescending(s => s.TrackNumber)
                .ThenByDescending(s => s.Title)
                .ThenByDescending(s => s.Id),
            SmartPlaylistSortOrder.AlbumAsc => query.OrderBy(s => s.Album != null ? s.Album.Title : string.Empty)
                .ThenBy(s => s.DiscNumber ?? 0)
                .ThenBy(s => s.TrackNumber)
                .ThenBy(s => s.Title)
                .ThenBy(s => s.Id),
            SmartPlaylistSortOrder.AlbumDesc => query.OrderByDescending(s => s.Album != null ? s.Album.Title : string.Empty)
                .ThenByDescending(s => s.DiscNumber ?? 0)
                .ThenByDescending(s => s.TrackNumber)
                .ThenByDescending(s => s.Title)
                .ThenByDescending(s => s.Id),
            SmartPlaylistSortOrder.YearAsc => query.OrderBy(s => s.Year)
                .ThenBy(s => s.PrimaryArtistName)
                .ThenBy(s => s.Album != null ? s.Album.Title : string.Empty)
                .ThenBy(s => s.DiscNumber ?? 0)
                .ThenBy(s => s.TrackNumber)
                .ThenBy(s => s.Title)
                .ThenBy(s => s.Id),
            SmartPlaylistSortOrder.YearDesc => query.OrderByDescending(s => s.Year)
                .ThenByDescending(s => s.PrimaryArtistName)
                .ThenByDescending(s => s.Album != null ? s.Album.Title : string.Empty)
                .ThenByDescending(s => s.DiscNumber ?? 0)
                .ThenByDescending(s => s.TrackNumber)
                .ThenByDescending(s => s.Title)
                .ThenByDescending(s => s.Id),
            SmartPlaylistSortOrder.PlayCountAsc => query.OrderBy(s => s.PlayCount).ThenBy(s => s.Title).ThenBy(s => s.Id),
            SmartPlaylistSortOrder.PlayCountDesc => query.OrderByDescending(s => s.PlayCount).ThenByDescending(s => s.Title).ThenByDescending(s => s.Id),
            SmartPlaylistSortOrder.LastPlayedAsc => query.OrderBy(s => s.LastPlayedDate).ThenBy(s => s.Title).ThenBy(s => s.Id),
            SmartPlaylistSortOrder.LastPlayedDesc => query.OrderByDescending(s => s.LastPlayedDate).ThenByDescending(s => s.Title).ThenByDescending(s => s.Id),
            SmartPlaylistSortOrder.DateAddedAsc => query.OrderBy(s => s.DateAddedToLibrary).ThenBy(s => s.Title).ThenBy(s => s.Id),
            SmartPlaylistSortOrder.DateAddedDesc => query.OrderByDescending(s => s.DateAddedToLibrary).ThenByDescending(s => s.Title).ThenByDescending(s => s.Id),
            SmartPlaylistSortOrder.TrackNumberAsc => query.OrderBy(s => s.Album != null ? s.Album.Title : string.Empty)
                .ThenBy(s => s.DiscNumber ?? 0)
                .ThenBy(s => s.TrackNumber)
                .ThenBy(s => s.Title)
                .ThenBy(s => s.Id),
            SmartPlaylistSortOrder.TrackNumberDesc => query.OrderByDescending(s => s.Album != null ? s.Album.Title : string.Empty)
                .ThenByDescending(s => s.DiscNumber ?? 0)
                .ThenByDescending(s => s.TrackNumber)
                .ThenByDescending(s => s.Title)
                .ThenByDescending(s => s.Id),
            SmartPlaylistSortOrder.DurationAsc => query.OrderBy(s => s.DurationTicks).ThenBy(s => s.Title).ThenBy(s => s.Id),
            SmartPlaylistSortOrder.DurationDesc => query.OrderByDescending(s => s.DurationTicks).ThenByDescending(s => s.Title).ThenByDescending(s => s.Id),
            SmartPlaylistSortOrder.BpmAsc => query.OrderBy(s => s.Bpm ?? 0).ThenBy(s => s.Title).ThenBy(s => s.Id),
            SmartPlaylistSortOrder.BpmDesc => query.OrderByDescending(s => s.Bpm ?? 0).ThenByDescending(s => s.Title).ThenByDescending(s => s.Id),
            SmartPlaylistSortOrder.Random => query.OrderBy(_ => EF.Functions.Random()),
            _ => query.OrderBy(s => s.Title).ThenBy(s => s.Id)
        };
    }

    #endregion

    #region Expression Helpers

    private static Expression<Func<Song, bool>> CombineSelector<T>(
        Expression<Func<Song, T>> selector,
        Expression<Func<T, bool>> predicate)
    {
        var param = Expression.Parameter(typeof(Song), "s");
        var selectorBody = ReplaceParameter(selector.Body, selector.Parameters[0], param);
        var predicateBody = ReplaceParameter(predicate.Body, predicate.Parameters[0], selectorBody);
        return Expression.Lambda<Func<Song, bool>>(predicateBody, param);
    }

    private static Expression ReplaceParameter(Expression expression, ParameterExpression oldParam, Expression newExpr)
    {
        return new ParameterReplacer(oldParam, newExpr).Visit(expression);
    }

    private static Expression<Func<Song, bool>> CombineWithOr(
        Expression<Func<Song, bool>> left,
        Expression<Func<Song, bool>> right)
    {
        var param = Expression.Parameter(typeof(Song), "s");
        var leftBody = ReplaceParameter(left.Body, left.Parameters[0], param);
        var rightBody = ReplaceParameter(right.Body, right.Parameters[0], param);
        var combined = Expression.OrElse(leftBody, rightBody);
        return Expression.Lambda<Func<Song, bool>>(combined, param);
    }

    private sealed class ParameterReplacer : ExpressionVisitor
    {
        private readonly Expression _newExpr;
        private readonly ParameterExpression _oldParam;

        public ParameterReplacer(ParameterExpression oldParam, Expression newExpr)
        {
            _oldParam = oldParam;
            _newExpr = newExpr;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            return node == _oldParam ? _newExpr : base.VisitParameter(node);
        }
    }

    #endregion

    #region Search

    private static IQueryable<Song> ApplySearchFilter(IQueryable<Song> query, string searchTerm)
    {
        var normalizedTerm = ArtistNameHelper.NormalizeStringCore(searchTerm) ?? searchTerm;
        var term = $"%{normalizedTerm}%";
        return query.Where(s =>
            EF.Functions.Like(s.Title, term)
            || EF.Functions.Like(s.ArtistName, term)
            || s.SongArtists.Any(sa => EF.Functions.Like(sa.Artist.Name, term))
            || (s.Album != null && (EF.Functions.Like(s.Album.Title, term) || EF.Functions.Like(s.Album.ArtistName, term) || s.Album.AlbumArtists.Any(aa => EF.Functions.Like(aa.Artist.Name, term)))));
    }



    #endregion
}

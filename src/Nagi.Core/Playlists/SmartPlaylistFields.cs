namespace Nagi.Core.Playlists;

public enum SmartPlaylistFieldKind
{
    Text,
    Integer,

    // Duration is in seconds.
    Decimal,

    Date,

    Boolean
}

/// <summary>
///     What kind of value each <see cref="SmartPlaylistField" /> holds, and so which operators a rule on it can use.
/// </summary>
public static class SmartPlaylistFields
{
    private static readonly IReadOnlyList<SmartPlaylistOperator> TextOperators =
    [
        SmartPlaylistOperator.Contains,
        SmartPlaylistOperator.DoesNotContain,
        SmartPlaylistOperator.Is,
        SmartPlaylistOperator.IsNot,
        SmartPlaylistOperator.StartsWith,
        SmartPlaylistOperator.EndsWith
    ];

    private static readonly IReadOnlyList<SmartPlaylistOperator> NumberOperators =
    [
        SmartPlaylistOperator.Equals,
        SmartPlaylistOperator.NotEquals,
        SmartPlaylistOperator.GreaterThan,
        SmartPlaylistOperator.LessThan,
        SmartPlaylistOperator.GreaterThanOrEqual,
        SmartPlaylistOperator.LessThanOrEqual,
        SmartPlaylistOperator.IsInRange
    ];

    private static readonly IReadOnlyList<SmartPlaylistOperator> DateOperators =
    [
        SmartPlaylistOperator.IsInTheLast,
        SmartPlaylistOperator.IsNotInTheLast,
        SmartPlaylistOperator.GreaterThan,
        SmartPlaylistOperator.LessThan,
        SmartPlaylistOperator.IsInRange
    ];

    private static readonly IReadOnlyList<SmartPlaylistOperator> BooleanOperators =
    [
        SmartPlaylistOperator.IsTrue,
        SmartPlaylistOperator.IsFalse
    ];

    public static SmartPlaylistFieldKind KindOf(SmartPlaylistField field) => field switch
    {
        SmartPlaylistField.Title or SmartPlaylistField.Artist or SmartPlaylistField.Album or SmartPlaylistField.Genre or
            SmartPlaylistField.Composer or SmartPlaylistField.Comment or SmartPlaylistField.Grouping or SmartPlaylistField.Tag => SmartPlaylistFieldKind.Text,

        SmartPlaylistField.PlayCount or SmartPlaylistField.SkipCount or SmartPlaylistField.Rating or SmartPlaylistField.Year or
            SmartPlaylistField.TrackNumber or SmartPlaylistField.DiscNumber or SmartPlaylistField.Bitrate or
            SmartPlaylistField.SampleRate => SmartPlaylistFieldKind.Integer,

        SmartPlaylistField.Bpm or SmartPlaylistField.Duration => SmartPlaylistFieldKind.Decimal,

        SmartPlaylistField.DateAdded or SmartPlaylistField.LastPlayed or SmartPlaylistField.FileCreatedDate or
            SmartPlaylistField.FileModifiedDate => SmartPlaylistFieldKind.Date,

        SmartPlaylistField.IsLoved or SmartPlaylistField.HasLyrics => SmartPlaylistFieldKind.Boolean,

        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null)
    };

    /// <returns>The operators in the order the editor offers them; the first is the default.</returns>
    public static IReadOnlyList<SmartPlaylistOperator> OperatorsFor(SmartPlaylistFieldKind kind) => kind switch
    {
        SmartPlaylistFieldKind.Text => TextOperators,
        SmartPlaylistFieldKind.Integer or SmartPlaylistFieldKind.Decimal => NumberOperators,
        SmartPlaylistFieldKind.Date => DateOperators,
        SmartPlaylistFieldKind.Boolean => BooleanOperators,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    // A stored rule can name a field this version doesn't know.
    public static bool Allows(SmartPlaylistField field, SmartPlaylistOperator ruleOperator) =>
        Enum.IsDefined(field) && OperatorsFor(KindOf(field)).Contains(ruleOperator);
}

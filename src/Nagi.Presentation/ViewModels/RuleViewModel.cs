using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Nagi.Core.Library;
using Nagi.Core.Playlists;
using Nagi.Presentation.Resources;
using CoreStrings = Nagi.Core.Resources.Strings;

namespace Nagi.Presentation.ViewModels;

public class RuleViewModel : ObservableObject
{
    public static IReadOnlyList<FieldOption> AvailableFields { get; } = new List<FieldOption>
    {
        new(SmartPlaylistField.Title, CoreStrings.Label_Title),
        new(SmartPlaylistField.Artist, CoreStrings.Label_Artist),
        new(SmartPlaylistField.Album, CoreStrings.Label_Album),
        new(SmartPlaylistField.Genre, CoreStrings.Label_Genre),
        new(SmartPlaylistField.Year, CoreStrings.Label_Year),
        new(SmartPlaylistField.Rating, Strings.Label_Rating),
        new(SmartPlaylistField.Duration, CoreStrings.Label_Duration),
        new(SmartPlaylistField.Bpm, CoreStrings.Label_Bpm),
        new(SmartPlaylistField.Comment, Strings.Label_Comment),
        new(SmartPlaylistField.Composer, Strings.Label_Composer),
        new(SmartPlaylistField.Grouping, Strings.Label_Grouping),
        new(SmartPlaylistField.PlayCount, CoreStrings.Label_PlayCount),
        new(SmartPlaylistField.SkipCount, Strings.Label_SkipCount),
        new(SmartPlaylistField.TrackNumber, CoreStrings.Label_TrackNumber),
        new(SmartPlaylistField.DiscNumber, Strings.Label_DiscNumber),
        new(SmartPlaylistField.Bitrate, Strings.Label_Bitrate),
        new(SmartPlaylistField.SampleRate, Strings.Label_SampleRate),
        new(SmartPlaylistField.IsLoved, Strings.Label_IsLoved),
        new(SmartPlaylistField.HasLyrics, Strings.Label_HasLyrics),
        new(SmartPlaylistField.DateAdded, CoreStrings.Label_DateAdded),
        new(SmartPlaylistField.LastPlayed, CoreStrings.Label_LastPlayed),
        new(SmartPlaylistField.FileCreatedDate, CoreStrings.Label_DateCreated),
        new(SmartPlaylistField.FileModifiedDate, CoreStrings.Label_DateModified)
    };

    // One list per kind, so switching between fields of the same kind keeps the same items.
    private static readonly IReadOnlyDictionary<SmartPlaylistFieldKind, IReadOnlyList<OperatorOption>> OperatorsByKind =
        Enum.GetValues<SmartPlaylistFieldKind>().ToDictionary(kind => kind, kind => (IReadOnlyList<OperatorOption>)
            SmartPlaylistFields.OperatorsFor(kind).Select(op => new OperatorOption(op, GetLabel(op, kind))).ToList());

    private FieldOption _selectedField = AvailableFields[0];
    private OperatorOption _selectedOperator;
    private string _value = string.Empty;
    private string _secondValue = string.Empty;

    public RuleViewModel()
    {
        AvailableOperators = OperatorsFor(_selectedField.Field);
        _selectedOperator = AvailableOperators[0];
    }

    public RuleViewModel(SmartPlaylistRule rule)
    {
        _selectedField = AvailableFields.First(f => f.Field == rule.Field);
        AvailableOperators = OperatorsFor(rule.Field);
        _selectedOperator = AvailableOperators.First(o => o.Operator == rule.Operator);
        _value = SmartPlaylistRuleValue.ToDisplayValue(rule.Field, rule.Operator, rule.Value);
        _secondValue = SmartPlaylistRuleValue.ToDisplayValue(rule.Field, rule.Operator, rule.SecondValue);
    }

    public IReadOnlyList<OperatorOption> AvailableOperators { get; private set; }

    public FieldOption SelectedField
    {
        get => _selectedField;
        set
        {
            // ComboBox can clear selection while its items are refreshed.
            if (value != null && SetProperty(ref _selectedField, value))
                UpdateOperatorsForField();
        }
    }

    public OperatorOption SelectedOperator
    {
        get => _selectedOperator;
        set
        {
            if (value != null && SetProperty(ref _selectedOperator, value))
            {
                OnPropertyChanged(nameof(IsRange));
                OnPropertyChanged(nameof(RequiresValue));
            }
        }
    }

    public bool IsRange => SelectedOperator.Operator == SmartPlaylistOperator.IsInRange;
    public bool RequiresValue => SelectedOperator.Operator is not (SmartPlaylistOperator.IsTrue or SmartPlaylistOperator.IsFalse);

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    public string SecondValue
    {
        get => _secondValue;
        set => SetProperty(ref _secondValue, value);
    }

    private void UpdateOperatorsForField()
    {
        AvailableOperators = OperatorsFor(_selectedField.Field);
        OnPropertyChanged(nameof(AvailableOperators));
        SelectedOperator = AvailableOperators.FirstOrDefault(o => o.Operator == _selectedOperator.Operator)
                           ?? AvailableOperators[0];
    }

    /// <summary>
    ///     The rule as typed; the smart playlist service converts its values to their stored form.
    /// </summary>
    public SmartPlaylistRule ToSmartPlaylistRule() => new()
    {
        Field = _selectedField.Field,
        Operator = _selectedOperator.Operator,
        Value = _value,
        SecondValue = _secondValue
    };

    public override string ToString() => $"{SelectedField.DisplayName} {SelectedOperator.DisplayName} {Value}".TrimEnd();

    private static IReadOnlyList<OperatorOption> OperatorsFor(SmartPlaylistField field) =>
        OperatorsByKind[SmartPlaylistFields.KindOf(field)];

    private static string GetLabel(SmartPlaylistOperator op, SmartPlaylistFieldKind kind) => op switch
    {
        SmartPlaylistOperator.Contains => Strings.Operator_Contains,
        SmartPlaylistOperator.DoesNotContain => Strings.Operator_DoesNotContain,
        SmartPlaylistOperator.Is => Strings.Operator_Is,
        SmartPlaylistOperator.IsNot => Strings.Operator_IsNot,
        SmartPlaylistOperator.StartsWith => Strings.Operator_StartsWith,
        SmartPlaylistOperator.EndsWith => Strings.Operator_EndsWith,
        SmartPlaylistOperator.Equals => Strings.Operator_Equals,
        SmartPlaylistOperator.NotEquals => Strings.Operator_NotEquals,
        SmartPlaylistOperator.GreaterThan => kind == SmartPlaylistFieldKind.Date ? Strings.Operator_IsAfter : Strings.Operator_GreaterThan,
        SmartPlaylistOperator.LessThan => kind == SmartPlaylistFieldKind.Date ? Strings.Operator_IsBefore : Strings.Operator_LessThan,
        SmartPlaylistOperator.GreaterThanOrEqual => Strings.Operator_GreaterThanOrEqual,
        SmartPlaylistOperator.LessThanOrEqual => Strings.Operator_LessThanOrEqual,
        SmartPlaylistOperator.IsInRange => Strings.Operator_IsInRange,
        SmartPlaylistOperator.IsInTheLast => Strings.Operator_IsInTheLast,
        SmartPlaylistOperator.IsNotInTheLast => Strings.Operator_IsNotInTheLast,
        SmartPlaylistOperator.IsTrue => Strings.Operator_IsTrue,
        SmartPlaylistOperator.IsFalse => Strings.Operator_IsFalse,
        _ => op.ToString()
    };
}

public record FieldOption(SmartPlaylistField Field, string DisplayName);

public record OperatorOption(SmartPlaylistOperator Operator, string DisplayName);

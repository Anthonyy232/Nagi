using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
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

    private static readonly IReadOnlyList<OperatorOption> TextOperators = new List<OperatorOption>
    {
        new(SmartPlaylistOperator.Contains, Strings.Operator_Contains),
        new(SmartPlaylistOperator.DoesNotContain, Strings.Operator_DoesNotContain),
        new(SmartPlaylistOperator.Is, Strings.Operator_Is),
        new(SmartPlaylistOperator.IsNot, Strings.Operator_IsNot),
        new(SmartPlaylistOperator.StartsWith, Strings.Operator_StartsWith),
        new(SmartPlaylistOperator.EndsWith, Strings.Operator_EndsWith)
    };

    private static readonly IReadOnlyList<OperatorOption> NumericOperators = new List<OperatorOption>
    {
        new(SmartPlaylistOperator.Equals, Strings.Operator_Equals),
        new(SmartPlaylistOperator.NotEquals, Strings.Operator_NotEquals),
        new(SmartPlaylistOperator.GreaterThan, Strings.Operator_GreaterThan),
        new(SmartPlaylistOperator.LessThan, Strings.Operator_LessThan),
        new(SmartPlaylistOperator.GreaterThanOrEqual, Strings.Operator_GreaterThanOrEqual),
        new(SmartPlaylistOperator.LessThanOrEqual, Strings.Operator_LessThanOrEqual),
        new(SmartPlaylistOperator.IsInRange, Strings.Operator_IsInRange)
    };

    private static readonly IReadOnlyList<OperatorOption> DateOperators = new List<OperatorOption>
    {
        new(SmartPlaylistOperator.IsInTheLast, Strings.Operator_IsInTheLast),
        new(SmartPlaylistOperator.IsNotInTheLast, Strings.Operator_IsNotInTheLast),
        new(SmartPlaylistOperator.GreaterThan, Strings.Operator_IsAfter),
        new(SmartPlaylistOperator.LessThan, Strings.Operator_IsBefore),
        new(SmartPlaylistOperator.IsInRange, Strings.Operator_IsInRange)
    };

    private static readonly IReadOnlyList<OperatorOption> BooleanOperators = new List<OperatorOption>
    {
        new(SmartPlaylistOperator.IsTrue, Strings.Operator_IsTrue),
        new(SmartPlaylistOperator.IsFalse, Strings.Operator_IsFalse)
    };

    private FieldOption _selectedField = AvailableFields[0];
    private OperatorOption _selectedOperator = TextOperators[0];
    private string _value = string.Empty;
    private string _secondValue = string.Empty;

    public RuleViewModel()
    {
    }

    public RuleViewModel(SmartPlaylistRule rule)
    {
        _selectedField = AvailableFields.First(f => f.Field == rule.Field);
        UpdateOperatorsForField();
        _selectedOperator = AvailableOperators.First(o => o.Operator == rule.Operator);
        _value = SmartPlaylistRuleValue.ToDisplayValue(rule.Field, rule.Operator, rule.Value);
        _secondValue = SmartPlaylistRuleValue.ToDisplayValue(rule.Field, rule.Operator, rule.SecondValue);
    }

    public IReadOnlyList<OperatorOption> AvailableOperators { get; private set; } = TextOperators;

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
        AvailableOperators = _selectedField.Field switch
        {
            SmartPlaylistField.Year or SmartPlaylistField.PlayCount or SmartPlaylistField.SkipCount or
            SmartPlaylistField.Rating or SmartPlaylistField.Duration or SmartPlaylistField.Bpm or
            SmartPlaylistField.TrackNumber or SmartPlaylistField.DiscNumber or SmartPlaylistField.Bitrate or
            SmartPlaylistField.SampleRate => NumericOperators,

            SmartPlaylistField.DateAdded or SmartPlaylistField.LastPlayed or
            SmartPlaylistField.FileCreatedDate or SmartPlaylistField.FileModifiedDate => DateOperators,

            SmartPlaylistField.IsLoved or SmartPlaylistField.HasLyrics => BooleanOperators,

            _ => TextOperators
        };

        OnPropertyChanged(nameof(AvailableOperators));
        SelectedOperator = AvailableOperators.FirstOrDefault(o => o.Operator == _selectedOperator.Operator)
                           ?? AvailableOperators[0];
    }

    public SmartPlaylistRule ToSmartPlaylistRule()
    {
        return new SmartPlaylistRule
        {
            Field = _selectedField.Field,
            Operator = _selectedOperator.Operator,
            Value = SmartPlaylistRuleValue.ToStoredValue(_selectedField.Field, _selectedOperator.Operator, _value),
            SecondValue = string.IsNullOrWhiteSpace(_secondValue) ? null
                : SmartPlaylistRuleValue.ToStoredValue(_selectedField.Field, _selectedOperator.Operator, _secondValue)
        };
    }

    public override string ToString() => $"{SelectedField.DisplayName} {SelectedOperator.DisplayName} {Value}".TrimEnd();
}

public record FieldOption(SmartPlaylistField Field, string DisplayName);

public record OperatorOption(SmartPlaylistOperator Operator, string DisplayName);

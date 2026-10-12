using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Nagi.Core.Helpers;
using Nagi.Core.Models;

namespace Nagi.WinUI.ViewModels;

public class RuleViewModel : ObservableObject
{
    public static IReadOnlyList<FieldOption> AvailableFields { get; } = new List<FieldOption>
    {
        new(SmartPlaylistField.Title, Nagi.Core.Resources.Strings.Label_Title),
        new(SmartPlaylistField.Artist, Nagi.Core.Resources.Strings.Label_Artist),
        new(SmartPlaylistField.Album, Nagi.Core.Resources.Strings.Label_Album),
        new(SmartPlaylistField.Genre, Nagi.Core.Resources.Strings.Label_Genre),
        new(SmartPlaylistField.Year, Nagi.Core.Resources.Strings.Label_Year),
        new(SmartPlaylistField.Rating, Nagi.WinUI.Resources.Strings.Label_Rating),
        new(SmartPlaylistField.Duration, Nagi.Core.Resources.Strings.Label_Duration),
        new(SmartPlaylistField.Bpm, Nagi.Core.Resources.Strings.Label_Bpm),
        new(SmartPlaylistField.Comment, Nagi.WinUI.Resources.Strings.Label_Comment),
        new(SmartPlaylistField.Composer, Nagi.WinUI.Resources.Strings.Label_Composer),
        new(SmartPlaylistField.Grouping, Nagi.WinUI.Resources.Strings.Label_Grouping),
        new(SmartPlaylistField.PlayCount, Nagi.Core.Resources.Strings.Label_PlayCount),
        new(SmartPlaylistField.SkipCount, Nagi.WinUI.Resources.Strings.Label_SkipCount),
        new(SmartPlaylistField.TrackNumber, Nagi.Core.Resources.Strings.Label_TrackNumber),
        new(SmartPlaylistField.DiscNumber, Nagi.WinUI.Resources.Strings.Label_DiscNumber),
        new(SmartPlaylistField.Bitrate, Nagi.WinUI.Resources.Strings.Label_Bitrate),
        new(SmartPlaylistField.SampleRate, Nagi.WinUI.Resources.Strings.Label_SampleRate),
        new(SmartPlaylistField.IsLoved, Nagi.WinUI.Resources.Strings.Label_IsLoved),
        new(SmartPlaylistField.HasLyrics, Nagi.WinUI.Resources.Strings.Label_HasLyrics),
        new(SmartPlaylistField.DateAdded, Nagi.Core.Resources.Strings.Label_DateAdded),
        new(SmartPlaylistField.LastPlayed, Nagi.Core.Resources.Strings.Label_LastPlayed),
        new(SmartPlaylistField.FileCreatedDate, Nagi.Core.Resources.Strings.Label_DateCreated),
        new(SmartPlaylistField.FileModifiedDate, Nagi.Core.Resources.Strings.Label_DateModified)
    };

    private static readonly IReadOnlyList<OperatorOption> TextOperators = new List<OperatorOption>
    {
        new(SmartPlaylistOperator.Contains, Nagi.WinUI.Resources.Strings.Operator_Contains),
        new(SmartPlaylistOperator.DoesNotContain, Nagi.WinUI.Resources.Strings.Operator_DoesNotContain),
        new(SmartPlaylistOperator.Is, Nagi.WinUI.Resources.Strings.Operator_Is),
        new(SmartPlaylistOperator.IsNot, Nagi.WinUI.Resources.Strings.Operator_IsNot),
        new(SmartPlaylistOperator.StartsWith, Nagi.WinUI.Resources.Strings.Operator_StartsWith),
        new(SmartPlaylistOperator.EndsWith, Nagi.WinUI.Resources.Strings.Operator_EndsWith)
    };

    private static readonly IReadOnlyList<OperatorOption> NumericOperators = new List<OperatorOption>
    {
        new(SmartPlaylistOperator.Equals, Nagi.WinUI.Resources.Strings.Operator_Equals),
        new(SmartPlaylistOperator.NotEquals, Nagi.WinUI.Resources.Strings.Operator_NotEquals),
        new(SmartPlaylistOperator.GreaterThan, Nagi.WinUI.Resources.Strings.Operator_GreaterThan),
        new(SmartPlaylistOperator.LessThan, Nagi.WinUI.Resources.Strings.Operator_LessThan),
        new(SmartPlaylistOperator.GreaterThanOrEqual, Nagi.WinUI.Resources.Strings.Operator_GreaterThanOrEqual),
        new(SmartPlaylistOperator.LessThanOrEqual, Nagi.WinUI.Resources.Strings.Operator_LessThanOrEqual),
        new(SmartPlaylistOperator.IsInRange, Nagi.WinUI.Resources.Strings.Operator_IsInRange)
    };

    private static readonly IReadOnlyList<OperatorOption> DateOperators = new List<OperatorOption>
    {
        new(SmartPlaylistOperator.IsInTheLast, Nagi.WinUI.Resources.Strings.Operator_IsInTheLast),
        new(SmartPlaylistOperator.IsNotInTheLast, Nagi.WinUI.Resources.Strings.Operator_IsNotInTheLast),
        new(SmartPlaylistOperator.GreaterThan, Nagi.WinUI.Resources.Strings.Operator_IsAfter),
        new(SmartPlaylistOperator.LessThan, Nagi.WinUI.Resources.Strings.Operator_IsBefore),
        new(SmartPlaylistOperator.IsInRange, Nagi.WinUI.Resources.Strings.Operator_IsInRange)
    };

    private static readonly IReadOnlyList<OperatorOption> BooleanOperators = new List<OperatorOption>
    {
        new(SmartPlaylistOperator.IsTrue, Nagi.WinUI.Resources.Strings.Operator_IsTrue),
        new(SmartPlaylistOperator.IsFalse, Nagi.WinUI.Resources.Strings.Operator_IsFalse)
    };

    public IReadOnlyList<FieldOption> AvailableFieldsList => AvailableFields;

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
        _value = SmartPlaylistRuleValue.ToDisplayValue(rule.Field, rule.Value);
        _secondValue = SmartPlaylistRuleValue.ToDisplayValue(rule.Field, rule.SecondValue);
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

}

public record FieldOption(SmartPlaylistField Field, string DisplayName);

public record OperatorOption(SmartPlaylistOperator Operator, string DisplayName);

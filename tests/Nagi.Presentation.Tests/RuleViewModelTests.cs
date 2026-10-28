using FluentAssertions;
using Nagi.Core.Models;
using System.Globalization;
using Nagi.Presentation.ViewModels;
using Xunit;

namespace Nagi.Presentation.Tests;

public class RuleViewModelTests
{
    [Theory]
    [InlineData(SmartPlaylistField.IsLoved, SmartPlaylistOperator.IsTrue, "", null)]
    [InlineData(SmartPlaylistField.Grouping, SmartPlaylistOperator.Contains, "live", null)]
    [InlineData(SmartPlaylistField.Year, SmartPlaylistOperator.IsInRange, "1990", "1999")]
    [InlineData(SmartPlaylistField.DateAdded, SmartPlaylistOperator.IsInRange, "2025-01-01T00:00:00.0000000", "2025-12-31T00:00:00.0000000")]
    public void ExistingRule_RoundTripsWithoutChangingItsMeaning(
        SmartPlaylistField field, SmartPlaylistOperator ruleOperator, string value, string? secondValue)
    {
        var viewModel = new RuleViewModel(new SmartPlaylistRule
        {
            Field = field,
            Operator = ruleOperator,
            Value = value,
            SecondValue = secondValue
        });

        var saved = viewModel.ToSmartPlaylistRule();

        saved.Field.Should().Be(field);
        saved.Operator.Should().Be(ruleOperator);
        saved.Value.Should().Be(value);
        saved.SecondValue.Should().Be(secondValue);
        viewModel.IsRange.Should().Be(ruleOperator == SmartPlaylistOperator.IsInRange);
        viewModel.RequiresValue.Should().Be(ruleOperator is not (SmartPlaylistOperator.IsTrue or SmartPlaylistOperator.IsFalse));
    }

    [Fact]
    public void ChangingField_PreservesCompatibleOperatorAndReplacesIncompatibleOperator()
    {
        var viewModel = new RuleViewModel(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Title,
            Operator = SmartPlaylistOperator.EndsWith,
            Value = "live"
        });
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        viewModel.SelectedField = RuleViewModel.AvailableFields.Single(f => f.Field == SmartPlaylistField.Album);
        viewModel.SelectedOperator.Operator.Should().Be(SmartPlaylistOperator.EndsWith);

        viewModel.SelectedField = RuleViewModel.AvailableFields.Single(f => f.Field == SmartPlaylistField.Year);
        viewModel.SelectedOperator.Operator.Should().Be(SmartPlaylistOperator.Equals);
        viewModel.AvailableOperators.Should().Contain(viewModel.SelectedOperator);
        viewModel.AvailableOperators.Should().NotContain(o => o.Operator == SmartPlaylistOperator.EndsWith);
        changed.Should().Contain(nameof(RuleViewModel.AvailableOperators));
        changed.Should().Contain(nameof(RuleViewModel.SelectedOperator));

        viewModel.Value = "2000";
        var rule = viewModel.ToSmartPlaylistRule();
        rule.Field.Should().Be(SmartPlaylistField.Year);
        rule.Operator.Should().Be(SmartPlaylistOperator.Equals);
        rule.Value.Should().Be("2000");

        viewModel.SelectedOperator = viewModel.AvailableOperators.Single(o => o.Operator == SmartPlaylistOperator.IsInRange);
        viewModel.IsRange.Should().BeTrue();
        viewModel.SelectedField = RuleViewModel.AvailableFields.Single(f => f.Field == SmartPlaylistField.IsLoved);
        viewModel.IsRange.Should().BeFalse();
        viewModel.RequiresValue.Should().BeFalse();
        changed.Should().Contain(nameof(RuleViewModel.IsRange));
        changed.Should().Contain(nameof(RuleViewModel.RequiresValue));
    }

    [Fact]
    public void TemporaryEmptyComboBoxSelection_DoesNotDropOrChangeRule()
    {
        var original = new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Year,
            Operator = SmartPlaylistOperator.GreaterThan,
            Value = "2000"
        };
        var viewModel = new RuleViewModel(original);

        viewModel.SelectedField = null!;
        viewModel.SelectedOperator = null!;

        var saved = viewModel.ToSmartPlaylistRule();
        saved.Field.Should().Be(original.Field);
        saved.Operator.Should().Be(original.Operator);
        saved.Value.Should().Be(original.Value);
    }

    [Theory]
    [InlineData(SmartPlaylistField.Bpm, "80,5", "90,5", "80.5", "90.5")]
    [InlineData(SmartPlaylistField.DateAdded, "01.02.2025", "03.04.2025", "2025-02-01T00:00:00.0000000", "2025-04-03T00:00:00.0000000")]
    public void LocalizedRange_RemainsStableWhenReopenedInAnotherCulture(
        SmartPlaylistField field, string value, string upper, string storedValue, string storedUpper)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var editor = new RuleViewModel(new SmartPlaylistRule
            {
                Field = field,
                Operator = SmartPlaylistOperator.IsInRange,
                Value = value,
                SecondValue = upper
            });
            var saved = editor.ToSmartPlaylistRule();
            saved.Value.Should().Be(storedValue);
            saved.SecondValue.Should().Be(storedUpper);

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var reopened = new RuleViewModel(saved).ToSmartPlaylistRule();
            reopened.Value.Should().Be(storedValue);
            reopened.SecondValue.Should().Be(storedUpper);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }
}

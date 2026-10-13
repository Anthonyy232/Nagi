using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.ViewModels;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Nagi.WinUI.Tests;

public class SmartPlaylistEditorViewModelTests : IDisposable
{
    private readonly ISmartPlaylistService _service = Substitute.For<ISmartPlaylistService>();
    private readonly IUIService _ui = Substitute.For<IUIService>();
    private readonly FakeTimeProvider _time = new();
    private readonly SmartPlaylistEditorViewModel _viewModel;

    public SmartPlaylistEditorViewModelTests()
    {
        _viewModel = new SmartPlaylistEditorViewModel(_service, _ui, new ImmediateDispatcher(),
            NullLogger<SmartPlaylistEditorViewModel>.Instance, _time);
    }

    public void Dispose() => _viewModel.Dispose();

    [Fact]
    public void Initialize_LoadsAnExistingPlaylistInRuleOrder()
    {
        var editing = new SmartPlaylist
        {
            Name = "Long tracks", MatchAllRules = false, SortOrder = SmartPlaylistSortOrder.DurationDesc,
            CoverImageUri = @"C:\covers\long.png",
            Rules =
            {
                new SmartPlaylistRule { Field = SmartPlaylistField.Genre, Operator = SmartPlaylistOperator.Is, Value = "Jazz", Order = 1 },
                new SmartPlaylistRule { Field = SmartPlaylistField.Title, Operator = SmartPlaylistOperator.Contains, Value = "Live", Order = 0 }
            }
        };

        _viewModel.Initialize(editing);

        _viewModel.Name.Should().Be("Long tracks");
        _viewModel.MatchLogicIndex.Should().Be(1);
        _viewModel.SelectedSortOption.SortOrder.Should().Be(SmartPlaylistSortOrder.DurationDesc);
        _viewModel.HasCoverImage.Should().BeTrue();
        _viewModel.Rules.Select(r => r.SelectedField.Field).Should().Equal(SmartPlaylistField.Title, SmartPlaylistField.Genre);
        _viewModel.HasNoRules.Should().BeFalse();
    }

    [Fact]
    public async Task Save_WithoutANameStaysOpenAndSavesNothing()
    {
        _viewModel.Initialize(null);
        _viewModel.Name = "   ";

        var result = await _viewModel.SaveAsync();

        result.Should().Be(SmartPlaylistSaveResult.NameMissing);
        await _service.DidNotReceiveWithAnyArgs().CreateSmartPlaylistAsync(default!);
    }

    [Fact]
    public async Task Save_NewPlaylistCreatesItWithRulesInDisplayOrder()
    {
        _service.CreateSmartPlaylistAsync(Arg.Any<SmartPlaylist>()).Returns(call => call.Arg<SmartPlaylist>());
        _viewModel.Initialize(null);
        _viewModel.Name = " Favourites ";
        _viewModel.AddRuleCommand.Execute(null);
        _viewModel.AddRuleCommand.Execute(null);
        _viewModel.Rules[1].SelectedField = RuleViewModel.AvailableFields.Single(f => f.Field == SmartPlaylistField.Album);

        var result = await _viewModel.SaveAsync();

        result.Should().Be(SmartPlaylistSaveResult.Saved);
        _viewModel.ResultPlaylist!.Name.Should().Be("Favourites");
        _viewModel.ResultPlaylist.Rules.Select(r => (r.Order, r.Field))
            .Should().Equal((0, SmartPlaylistField.Title), (1, SmartPlaylistField.Album));
        await _service.DidNotReceiveWithAnyArgs().UpdateSmartPlaylistAsync(default!);
    }

    [Fact]
    public async Task Save_ExistingPlaylistUpdatesItUnderTheSameId()
    {
        var editing = new SmartPlaylist { Name = "Old" };
        _service.UpdateSmartPlaylistAsync(Arg.Any<SmartPlaylist>()).Returns(call => call.Arg<SmartPlaylist>());
        _viewModel.Initialize(editing);
        _viewModel.Name = "New";

        var result = await _viewModel.SaveAsync();

        result.Should().Be(SmartPlaylistSaveResult.Saved);
        await _service.Received(1).UpdateSmartPlaylistAsync(Arg.Is<SmartPlaylist>(p => p.Id == editing.Id && p.Name == "New"));
    }

    [Fact]
    public async Task Save_DuplicateNameIsReportedAndKeepsTheEditorOpen()
    {
        _service.CreateSmartPlaylistAsync(Arg.Any<SmartPlaylist>())
            .ThrowsAsync(new DbUpdateException("duplicate", new SqliteException("UNIQUE constraint failed", 19, 2067)));
        _viewModel.Initialize(null);
        _viewModel.Name = "Taken";

        var result = await _viewModel.SaveAsync();

        result.Should().Be(SmartPlaylistSaveResult.DuplicateName);
        _viewModel.MatchCountText.Should().Be(Nagi.Presentation.Resources.Strings.SmartPlaylist_Error_DuplicateName);
    }

    [Fact]
    public async Task EditingRules_RecountsMatchesAfterTheDebounce()
    {
        _service.GetMatchingSongCountAsync(Arg.Any<SmartPlaylist>(), token: Arg.Any<CancellationToken>()).Returns(7);
        _viewModel.Initialize(new SmartPlaylist { Name = "Mix" });
        _time.Advance(TimeSpan.FromMilliseconds(300));
        await WaitForAsync(() => _viewModel.MatchCountText != Nagi.Presentation.Resources.Strings.SmartPlaylist_Status_Calculating);
        _service.ClearReceivedCalls();

        _viewModel.AddRuleCommand.Execute(null);
        _time.Advance(TimeSpan.FromMilliseconds(300));

        await WaitForAsync(() => _service.ReceivedCalls().Any());
        await _service.Received(1).GetMatchingSongCountAsync(Arg.Is<SmartPlaylist>(p => p.Rules.Count == 1),
            token: Arg.Any<CancellationToken>());
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met.");
            await Task.Delay(10);
        }
    }
}

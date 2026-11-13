using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Lyrics.Romanization;
using Nagi.Presentation.Settings;
using Nagi.Presentation.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Presentation.Tests.Settings;

public class RomanizationPacksSettingsViewModelTests
{
    [Fact]
    public async Task RefreshDuringALoad_ShowsOnlyTheRefreshedPacksAndStopsLoading()
    {
        var manager = Substitute.For<IRomanizationPackManager>();
        var load = new TaskCompletionSource<IReadOnlyList<RomanizationPackView>>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.GetAvailablePacksAsync(false, Arg.Any<CancellationToken>()).Returns(load.Task);
        manager.GetAvailablePacksAsync(true, Arg.Any<CancellationToken>()).Returns([Pack("refreshed")]);
        using var viewModel = new RomanizationPacksSettingsViewModel(manager, new ImmediateDispatcher(),
            NullLogger<RomanizationPacksSettingsViewModel>.Instance);

        var loading = viewModel.LoadRomanizationPacksAsync();
        await viewModel.RefreshRomanizationPacksCommand.ExecuteAsync(null);
        load.SetResult([Pack("stale")]);
        await loading;

        viewModel.RomanizationPacks.Select(pack => pack.Id).Should().Equal("refreshed");
        viewModel.IsLoadingRomanizationPacks.Should().BeFalse();
    }

    private static RomanizationPackView Pack(string id) =>
        new() { CatalogEntry = new RomanizationPackCatalogEntry { Id = id } };
}

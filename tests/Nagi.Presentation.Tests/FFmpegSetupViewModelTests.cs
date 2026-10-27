using FluentAssertions;
using Nagi.Presentation.ViewModels;
using Xunit;

namespace Nagi.Presentation.Tests;

public class FFmpegSetupViewModelTests
{
    public static TheoryData<Func<Task<bool>>, FFmpegSetupStatus, bool> Rechecks => new()
    {
        { () => Task.FromResult(true), FFmpegSetupStatus.Detected, true },
        { () => Task.FromResult(false), FFmpegSetupStatus.StillNotDetected, false }
    };

    [Theory]
    [MemberData(nameof(Rechecks))]
    public async Task Recheck_ReportsWhatTheCheckFound(Func<Task<bool>> check, FFmpegSetupStatus status, bool installed)
    {
        var viewModel = new FFmpegSetupViewModel(check);

        (await viewModel.RecheckAsync()).Should().Be(installed);

        viewModel.Status.Should().Be(status);
    }
}

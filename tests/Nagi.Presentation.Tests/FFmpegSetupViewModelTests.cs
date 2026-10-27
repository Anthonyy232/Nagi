using FluentAssertions;
using Nagi.Presentation.ViewModels;
using Xunit;

namespace Nagi.Presentation.Tests;

public class FFmpegSetupViewModelTests
{
    [Fact]
    public async Task Recheck_ShowsCheckingUntilTheCheckCompletes()
    {
        var check = new TaskCompletionSource<bool>();
        var viewModel = new FFmpegSetupViewModel(() => check.Task);

        var recheck = viewModel.RecheckAsync();
        viewModel.Status.Should().Be(FFmpegSetupStatus.Checking);

        check.SetResult(true);

        (await recheck).Should().BeTrue();
        viewModel.Status.Should().Be(FFmpegSetupStatus.Detected);
    }
}

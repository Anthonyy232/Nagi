using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Models;
using Nagi.Core.Services.Data;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.ViewModels;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Nagi.Presentation.Tests;

public class OnboardingViewModelTests
{
    private const string FolderPath = @"C:\Music";

    private readonly LibraryFakes _library = new();
    private readonly IUIService _ui = Substitute.For<IUIService>();
    private readonly IApplicationLifecycle _lifecycle = Substitute.For<IApplicationLifecycle>();
    private readonly OnboardingViewModel _viewModel;

    public OnboardingViewModelTests()
    {
        _ui.PickSingleFolderAsync().Returns(FolderPath);
        _library.Writer.AddFolderAsync(FolderPath).Returns(new Folder { Name = "Music", Path = FolderPath });
        _viewModel = new OnboardingViewModel(_library.Writer, _library.Scanner, _ui, _lifecycle,
            NullLogger<OnboardingViewModel>.Instance);
        _viewModel.GoNextCommand.Execute(null);
    }

    [Fact]
    public async Task ChoosingAFolder_MovesOnWhileScanningAndOnlyAllowsFinishingOnceTheLibraryIsReady()
    {
        var scan = new TaskCompletionSource();
        _library.Scanner.ScanFolderForMusicAsync(FolderPath, Arg.Any<IProgress<ScanProgress>>())
            .Returns(scan.Task);

        var choosing = _viewModel.ChooseFolderCommand.ExecuteAsync(null);

        _viewModel.CurrentStep.Should().Be(OnboardingStep.Appearance);
        _viewModel.IsScanning.Should().BeTrue();
        _viewModel.SkipSetupCommand.CanExecute(null).Should().BeTrue();
        _viewModel.FinishCommand.CanExecute(null).Should().BeFalse();
        // Going back to the welcome step would offer a language change, which restarts the app mid-scan.
        _viewModel.GoToStepCommand.CanExecute(OnboardingStep.Library).Should().BeTrue();
        _viewModel.GoToStepCommand.CanExecute(OnboardingStep.Welcome).Should().BeFalse();

        scan.SetResult();
        await choosing;

        _viewModel.IsLibraryReady.Should().BeTrue();
        _viewModel.FinishCommand.CanExecute(null).Should().BeTrue();
        await _viewModel.FinishCommand.ExecuteAsync(null);
        await _lifecycle.Received(1).NavigateToMainContentAsync();
    }

    [Fact]
    public async Task LibraryStep_StaysUntilAFolderIsChosen()
    {
        _ui.PickSingleFolderAsync().Returns((string?)null);

        _viewModel.GoNextCommand.CanExecute(null).Should().BeFalse();
        await _viewModel.ChooseFolderCommand.ExecuteAsync(null);

        _viewModel.CurrentStep.Should().Be(OnboardingStep.Library);
        await _library.Writer.DidNotReceiveWithAnyArgs().AddFolderAsync(default!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedFolder_KeepsTheStepsOpenButOnlyFinishesAfterAnotherFolderIsChosen(bool cannotAddFolder)
    {
        if (cannotAddFolder)
            _library.Writer.AddFolderAsync(FolderPath).Returns((Folder?)null);
        else
            _library.Scanner.ScanFolderForMusicAsync(FolderPath, Arg.Any<IProgress<ScanProgress>>())
                .ThrowsAsync(new IOException("unreadable"));

        await _viewModel.ChooseFolderCommand.ExecuteAsync(null);

        _viewModel.HasLibraryFailed.Should().BeTrue();
        _viewModel.SkipSetupCommand.CanExecute(null).Should().BeTrue();
        _viewModel.FinishCommand.CanExecute(null).Should().BeFalse();
        _viewModel.GoToStepCommand.CanExecute(OnboardingStep.Welcome).Should().BeTrue();

        _viewModel.SkipSetupCommand.Execute(null);
        _library.Writer.AddFolderAsync(FolderPath).Returns(new Folder { Name = "Music", Path = FolderPath });
        _library.Scanner.ScanFolderForMusicAsync(FolderPath, Arg.Any<IProgress<ScanProgress>>())
            .Returns(Task.CompletedTask);
        await _viewModel.ChooseFolderCommand.ExecuteAsync(null);

        _viewModel.CurrentStep.Should().Be(OnboardingStep.Finish);
        _viewModel.FinishCommand.CanExecute(null).Should().BeTrue();
    }
}

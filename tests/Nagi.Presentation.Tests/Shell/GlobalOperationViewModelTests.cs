using AwesomeAssertions;
using Nagi.Presentation.Shell;
using Xunit;

namespace Nagi.Presentation.Tests.Shell;

public class GlobalOperationViewModelTests
{
    [Fact]
    public void Cancel_RunsTheOfferedCancellationOnceAndWithdrawsIt()
    {
        var operation = new GlobalOperationViewModel();
        var cancellations = 0;
        using var scope = operation.TryBegin("Scanning", () => cancellations++);
        operation.CancelCommand.CanExecute(null).Should().BeTrue();

        operation.CancelCommand.Execute(null);
        operation.CancelCommand.Execute(null);

        cancellations.Should().Be(1);
        operation.IsCancellable.Should().BeFalse();
        operation.CancelCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void TryBegin_WhileAnotherOperationRuns_LeavesTheRunningOneInCharge()
    {
        var operation = new GlobalOperationViewModel();
        var firstCancelled = false;
        using var first = operation.TryBegin("Scanning", () => firstCancelled = true);

        operation.TryBegin("Syncing").Should().BeNull();

        operation.StatusMessage.Should().Be("Scanning");
        operation.CancelCommand.Execute(null);
        firstCancelled.Should().BeTrue();
    }
}

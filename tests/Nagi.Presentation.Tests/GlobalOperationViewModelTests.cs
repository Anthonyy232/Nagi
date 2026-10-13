using FluentAssertions;
using Nagi.Presentation.ViewModels;
using Xunit;

namespace Nagi.Presentation.Tests;

public class GlobalOperationViewModelTests
{
    [Fact]
    public void Cancel_RunsTheOfferedCancellationOnceAndWithdrawsIt()
    {
        var operation = new GlobalOperationViewModel();
        var cancellations = 0;
        operation.SetCancellation(() => cancellations++);
        operation.CancelCommand.CanExecute(null).Should().BeTrue();

        operation.CancelCommand.Execute(null);
        operation.CancelCommand.Execute(null);

        cancellations.Should().Be(1);
        operation.IsCancellable.Should().BeFalse();
        operation.CancelCommand.CanExecute(null).Should().BeFalse();
    }
}

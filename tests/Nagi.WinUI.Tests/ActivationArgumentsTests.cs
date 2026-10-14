using FluentAssertions;
using Xunit;

namespace Nagi.WinUI.Tests;

public class ActivationArgumentsTests
{
    [Fact]
    public void LaunchWithoutFile_DoesNotTreatStartupFlagAsPlaybackRequest()
    {
        Program.TryGetFilePathFromArgs([]).Should().BeNull();
        Program.TryGetFilePathFromArgs(["--startup"]).Should().BeNull();
    }

    [Fact]
    public void LaunchWithFlagsAndFile_UsesTheFileArgument()
    {
        var filePath = typeof(ActivationArgumentsTests).Assembly.Location;
        Program.TryGetFilePathFromArgs(["--startup", filePath]).Should().Be(filePath);
    }
}

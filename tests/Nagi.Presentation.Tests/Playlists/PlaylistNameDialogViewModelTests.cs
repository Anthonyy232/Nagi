using FluentAssertions;
using Nagi.Presentation.Common;
using Nagi.Presentation.Playlists;
using NSubstitute;
using Xunit;

namespace Nagi.Presentation.Tests.Playlists;

public class PlaylistNameDialogViewModelTests
{
    [Theory]
    [InlineData("Road Trip", true)]
    [InlineData(" Mix ", false)]
    [InlineData("   ", false)]
    public void Rename_RequiresANewNonBlankName(string newName, bool expected) =>
        new PlaylistNameDialogViewModel(Substitute.For<IUIService>(), "Mix") { Name = newName }.CanAccept.Should().Be(expected);
}

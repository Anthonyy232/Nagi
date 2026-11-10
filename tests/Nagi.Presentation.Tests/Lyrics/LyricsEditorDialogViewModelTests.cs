using FluentAssertions;
using Nagi.Presentation.Lyrics;
using Xunit;

namespace Nagi.Presentation.Tests.Lyrics;

public class LyricsEditorDialogViewModelTests
{
    [Theory]
    [InlineData("line one\r\nline two", false)]
    [InlineData("line one\nline three", true)]
    [InlineData("  ", false)]
    public void LyricsEditor_IgnoresLineEndingChanges(string edited, bool expected) =>
        new LyricsEditorDialogViewModel("line one\nline two", canRemove: false) { Lyrics = edited }.CanSave
            .Should().Be(expected);
}

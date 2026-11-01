using FluentAssertions;
using Nagi.Core.Helpers;
using Xunit;

namespace Nagi.Core.Tests;

public class UrlRedactionTests
{
    [Theory]
    [InlineData("open of `https://music.example.com/rest/stream.view?id=1&u=alex&t=abc&s=def' failed",
        "open of `https://music.example.com/rest/stream.view?…' failed")]
    [InlineData("outgoing request: GET /rest/stream.view?u=alex&t=abc&s=def HTTP/1.1",
        "outgoing request: GET /rest/stream.view?… HTTP/1.1")]
    [InlineData(@"open of `file:///C:/Music/Is%20this%20it.flac?' failed",
        @"open of `file:///C:/Music/Is%20this%20it.flac?' failed")]
    public void RemoveQueries_HidesQueriesThatCouldCarryASignIn(string text, string expected) =>
        UrlRedaction.RemoveQueries(text).Should().Be(expected);
}

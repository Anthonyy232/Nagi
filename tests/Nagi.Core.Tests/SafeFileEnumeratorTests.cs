using FluentAssertions;
using Nagi.Core.Helpers;
using Xunit;

namespace Nagi.Core.Tests;

public sealed class SafeFileEnumeratorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"NagiFileEnumeration_{Guid.NewGuid():N}");

    public SafeFileEnumeratorTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void EnumerateFilesWithLastWriteTime_RecursesButSkipsSystemMetadataDirectories()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_root, "Album"));
        var excluded = Directory.CreateDirectory(Path.Combine(_root, "$RECYCLE.BIN"));
        var rootSong = Path.Combine(_root, "root.mp3");
        var nestedSong = Path.Combine(nested.FullName, "nested.mp3");
        File.WriteAllText(rootSong, "root");
        File.WriteAllText(nestedSong, "nested");
        File.WriteAllText(Path.Combine(excluded.FullName, "ghost.mp3"), "excluded");
        File.WriteAllText(Path.Combine(nested.FullName, "notes.txt"), "not audio");

        var files = SafeFileEnumerator
            .EnumerateFilesWithLastWriteTime(_root, "*.mp3", SearchOption.AllDirectories)
            .Select(item => item.Path)
            .ToList();

        files.Should().BeEquivalentTo(rootSong, nestedSong);
    }

    [Fact]
    public void EnumerateFilesWithLastWriteTime_TopDirectoryOnlyDoesNotDescend()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_root, "Album"));
        var rootSong = Path.Combine(_root, "root.flac");
        File.WriteAllText(rootSong, "root");
        File.WriteAllText(Path.Combine(nested.FullName, "nested.flac"), "nested");

        var files = SafeFileEnumerator
            .EnumerateFilesWithLastWriteTime(_root, "*.flac", SearchOption.TopDirectoryOnly)
            .Select(item => item.Path);

        files.Should().ContainSingle().Which.Should().Be(rootSong);
    }

    [Fact]
    public void EnumerateFilesWithLastWriteTime_MissingRootReturnsEmptySequence()
    {
        var missing = Path.Combine(_root, "missing");

        SafeFileEnumerator
            .EnumerateFilesWithLastWriteTime(missing, "*", SearchOption.AllDirectories)
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData(FileAttributes.ReparsePoint, true)]
    [InlineData(FileAttributes.Hidden | FileAttributes.System, true)]
    [InlineData(FileAttributes.Hidden, false)]
    [InlineData(FileAttributes.System, false)]
    [InlineData(FileAttributes.Directory, false)]
    public void IsExcludedAttributes_RejectsCycleAndOperatingSystemDirectories(
        FileAttributes attributes,
        bool expected)
    {
        SafeFileEnumerator.IsExcludedAttributes(attributes).Should().Be(expected);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}

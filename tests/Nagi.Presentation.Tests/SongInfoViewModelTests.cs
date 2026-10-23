using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.ViewModels;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Nagi.Presentation.Tests;

public class SongInfoViewModelTests : IDisposable
{
    private readonly ILibraryReader _reader = Substitute.For<ILibraryReader>();
    private readonly IFileSystemService _fileSystem = Substitute.For<IFileSystemService>();
    private readonly SongInfoViewModel _viewModel;
    private readonly CultureInfo _originalCulture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _originalUiCulture = CultureInfo.CurrentUICulture;
    private readonly string _tempFile = Path.GetTempFileName();

    public SongInfoViewModelTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        _viewModel = new SongInfoViewModel(_reader, _fileSystem, NullLogger<SongInfoViewModel>.Instance);
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _originalCulture;
        CultureInfo.CurrentUICulture = _originalUiCulture;
        File.Delete(_tempFile);
    }

    [Fact]
    public async Task Load_FormatsNumbersAndSkipsMissingValues()
    {
        File.WriteAllBytes(_tempFile, new byte[3 * 1024 * 1024 / 2]);
        var song = new Song
        {
            FilePath = @"C:\Music\cobalt.flac",
            TrackNumber = 2,
            TrackCount = 12,
            SampleRate = 44100,
            ReplayGainTrackGain = -3.214
        };
        _reader.GetSongByIdAsync(song.Id).Returns(song);
        _fileSystem.GetFileInfo(song.FilePath).Returns(new FileInfo(_tempFile));

        await _viewModel.LoadAsync(song);

        var fields = _viewModel.Sections.SelectMany(s => s.Fields).ToDictionary(f => f.Label, f => f.Value);
        fields.Should().Contain(new Dictionary<string, string>
        {
            ["Track Number"] = "2 of 12",
            ["Sample Rate"] = "44,100 Hz",
            ["ReplayGain"] = "-3.21 dB",
            ["Size"] = "1.5 MB"
        });
        fields.Should().NotContainKeys("Composer", "Last Played");
    }

    [Fact]
    public async Task Load_FallsBackToTheListSongWhenItsDetailsCannotBeRead()
    {
        var listSong = new Song { Title = "Cobalt", ArtistName = "MOB CHOIR", FilePath = @"C:\Missing\cobalt.mp3" };
        _reader.GetSongByIdAsync(listSong.Id).ThrowsAsync(new InvalidOperationException("database is locked"));
        _fileSystem.GetFileInfo(listSong.FilePath).Returns(new FileInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())));

        await _viewModel.LoadAsync(listSong);

        _viewModel.Title.Should().Be("Cobalt");
        _viewModel.ArtistName.Should().Be("MOB CHOIR");
        _viewModel.Sections.SelectMany(s => s.Fields).Should().NotContain(f => f.Label == "Size");
    }
}

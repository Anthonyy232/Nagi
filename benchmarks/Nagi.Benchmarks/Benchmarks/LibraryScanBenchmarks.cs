using BenchmarkDotNet.Attributes;
using Microsoft.EntityFrameworkCore;
using Nagi.Benchmarks.Helpers;

namespace Nagi.Benchmarks.Benchmarks;

[MemoryDiagnoser]
public class LibraryScanBenchmarks
{
    private LibraryBenchmarkFixture _fixture = null!;

    [Params(100, 500, 10_000)]
    public int SongCount;

    [GlobalSetup]
    public async Task Setup()
    {
        _fixture = new LibraryBenchmarkFixture();
        SyntheticAudioGenerator.GenerateLibrary(_fixture.RootPath, SongCount);
        await _fixture.Library.ScanFolderForMusicAsync(_fixture.RootPath);
        VerifyScan();
    }

    [GlobalCleanup]
    public void Cleanup() => _fixture.Dispose();

    [IterationSetup(Target = nameof(InitialScan))]
    public void ResetLibrary()
    {
        using var context = _fixture.ContextFactory.CreateDbContext();
        context.Songs.ExecuteDelete();
        context.Albums.ExecuteDelete();
        context.Artists.ExecuteDelete();
    }

    [IterationCleanup]
    public void VerifyScan()
    {
        using var context = _fixture.ContextFactory.CreateDbContext();
        var indexedCount = context.Songs.Count();
        var invalidCount = context.Songs.Count(song => song.DurationTicks <= 0);
        if (indexedCount != SongCount || invalidCount != 0)
            throw new InvalidOperationException($"Indexed {indexedCount}/{SongCount} songs, with {invalidCount} invalid durations.");
    }

    [Benchmark]
    public Task InitialScan() => _fixture.Library.ScanFolderForMusicAsync(_fixture.RootPath);

    [Benchmark]
    public Task RescanNoChanges() => _fixture.Library.ScanFolderForMusicAsync(_fixture.RootPath);
}

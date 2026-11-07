using BenchmarkDotNet.Attributes;
using Microsoft.EntityFrameworkCore;
using Nagi.Benchmarks.Helpers;

namespace Nagi.Benchmarks.Benchmarks;

[MemoryDiagnoser]
public class LibraryScanBenchmarks
{
    private LibraryBenchmarkFixture _fixture = null!;
    private Guid _folderId;

    [Params(100, 500, 10_000)]
    public int SongCount;

    [GlobalSetup]
    public async Task Setup()
    {
        _fixture = new LibraryBenchmarkFixture();
        SyntheticAudioGenerator.GenerateLibrary(_fixture.RootPath, SongCount);
        _folderId = (await _fixture.Folders.AddFolderAsync(_fixture.RootPath))!.Id;
        await _fixture.Folders.RescanFolderForMusicAsync(_folderId);
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
    public Task InitialScan() => _fixture.Folders.RescanFolderForMusicAsync(_folderId);

    [Benchmark]
    public Task RescanNoChanges() => _fixture.Folders.RescanFolderForMusicAsync(_folderId);
}

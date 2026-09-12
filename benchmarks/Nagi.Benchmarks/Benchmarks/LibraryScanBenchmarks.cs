using BenchmarkDotNet.Attributes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Benchmarks.Helpers;
using Nagi.Core.Data;
using Nagi.Core.Helpers;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
using NSubstitute;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Nagi.Benchmarks.Benchmarks;

[MemoryDiagnoser]
public class LibraryScanBenchmarks
{
    private string _testPath = null!;
    private LibraryService _libraryService = null!;
    private ServiceProvider _serviceProvider = null!;

    [Params(100, 500)]
    public int SongCount;

    [GlobalSetup]
    public async Task Setup()
    {
        _testPath = Path.Combine(Path.GetTempPath(), "NagiBenchmarks", Guid.NewGuid().ToString());
        SyntheticAudioGenerator.GenerateLibrary(_testPath, SongCount);

        var services = new ServiceCollection();

        var dbPath = Path.Combine(_testPath, "test.db");
        services.AddDbContextFactory<MusicDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));

        services.AddSingleton<IFileSystemService, TestFileSystemService>();
        services.AddSingleton<IPathConfiguration>(sp => {
            var pathConfig = Substitute.For<IPathConfiguration>();
            pathConfig.AlbumArtCachePath.Returns(Path.Combine(_testPath, "Art"));
            pathConfig.ArtistImageCachePath.Returns(Path.Combine(_testPath, "Artists"));
            pathConfig.LrcCachePath.Returns(Path.Combine(_testPath, "Lrc"));
            return pathConfig;
        });

        services.AddSingleton<IImageProcessor>(Substitute.For<IImageProcessor>());
        services.AddSingleton<ILastFmMetadataService>(Substitute.For<ILastFmMetadataService>());

        var settingsService = Substitute.For<ISettingsService>();
        settingsService.GetVolumeNormalizationEnabledAsync().Returns(false);
        services.AddSingleton<ISettingsService>(settingsService);

        services.AddSingleton<IReplayGainService>(Substitute.For<IReplayGainService>());
        services.AddSingleton<IMusicBrainzService>(Substitute.For<IMusicBrainzService>());
        services.AddSingleton<IFanartTvService>(Substitute.For<IFanartTvService>());
        services.AddSingleton<ITheAudioDbService>(Substitute.For<ITheAudioDbService>());
        services.AddSingleton<IApiKeyService>(Substitute.For<IApiKeyService>());
        services.AddSingleton<IProviderPipelineProvider>(Substitute.For<IProviderPipelineProvider>());
        services.AddSingleton<IMetadataService, AtlMetadataService>();
        services.AddHttpClient();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));

        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MusicDbContext>>();
        using var context = factory.CreateDbContext();
        context.Database.EnsureCreated();

        _libraryService = ActivatorUtilities.CreateInstance<LibraryService>(_serviceProvider);
        await _libraryService.ScanFolderForMusicAsync(_testPath);
        VerifyScan();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _libraryService?.Dispose();
        if (_serviceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }

        SqliteConnection.ClearAllPools();

        if (Directory.Exists(_testPath))
        {
            try
            {
                Directory.Delete(_testPath, true);
            }
            catch (IOException)
            {
                Thread.Sleep(100);
                if (Directory.Exists(_testPath))
                {
                    try { Directory.Delete(_testPath, true); } catch { }
                }
            }
        }
    }

    [IterationSetup(Target = nameof(InitialScan))]
    public void ResetLibrary()
    {
        using var scope = _serviceProvider.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MusicDbContext>>();
        using var context = factory.CreateDbContext();
        context.Songs.ExecuteDelete();
        context.Albums.ExecuteDelete();
        context.Artists.ExecuteDelete();
    }

    [IterationCleanup]
    public void VerifyScan()
    {
        var factory = _serviceProvider.GetRequiredService<IDbContextFactory<MusicDbContext>>();
        using var context = factory.CreateDbContext();
        var indexedCount = context.Songs.Count();
        var invalidCount = context.Songs.Count(song => song.DurationTicks <= 0);
        if (indexedCount != SongCount || invalidCount != 0)
            throw new InvalidOperationException($"Indexed {indexedCount}/{SongCount} songs, with {invalidCount} invalid durations.");
    }

    [Benchmark]
    public Task InitialScan() => _libraryService.ScanFolderForMusicAsync(_testPath);

    [Benchmark]
    public async Task RescanNoChanges()
    {
        await _libraryService.ScanFolderForMusicAsync(_testPath);
    }

    private class TestFileSystemService : IFileSystemService
    {
        public void CreateDirectory(string path) => Directory.CreateDirectory(path);
        public void DeleteDirectory(string path, bool recursive) => Directory.Delete(path, recursive);
        public void DeleteFile(string path) => File.Delete(path);
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption) => Directory.EnumerateFiles(path, searchPattern, searchOption);
        public IEnumerable<(string Path, DateTime LastWriteTimeUtc)> EnumerateFilesWithLastWriteTime(string path, string searchPattern, SearchOption searchOption) =>
            new DirectoryInfo(path).EnumerateFiles(searchPattern, searchOption).Select(fi => (fi.FullName, fi.LastWriteTimeUtc));
        public bool FileExists(string path) => File.Exists(path);
        public string[] GetFiles(string path, string searchPattern) => Directory.GetFiles(path, searchPattern);
        public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);
        public string GetFileNameWithoutExtension(string path) => Path.GetFileNameWithoutExtension(path);
        public string GetFileName(string path) => Path.GetFileName(path);
        public string? GetDirectoryName(string path) => Path.GetDirectoryName(path);
        public string GetExtension(string path) => Path.GetExtension(path);
        public string Combine(params string[] paths) => Path.Combine(paths);
        public Task<byte[]> ReadAllBytesAsync(string path) => File.ReadAllBytesAsync(path);
        public Task<string> ReadAllTextAsync(string path) => File.ReadAllTextAsync(path);
        public Task WriteAllBytesAsync(string path, byte[] bytes) => File.WriteAllBytesAsync(path, bytes);
        public Task WriteAllTextAsync(string path, string contents) => File.WriteAllTextAsync(path, contents);
        public void CopyFile(string source, string dest, bool overwrite) => File.Copy(source, dest, overwrite);
        public void MoveFile(string source, string dest, bool overwrite) => File.Move(source, dest, overwrite);
        public FileInfo GetFileInfo(string path) => new FileInfo(path);
        public bool IsHiddenOrSystemFile(string path)
        {
            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
        }
        public string NormalizePath(string path) => Nagi.Core.Helpers.PathCanonicalizer.Normalize(path ?? string.Empty);
        public bool IsNetworkPath(string path) => false;
    }
}

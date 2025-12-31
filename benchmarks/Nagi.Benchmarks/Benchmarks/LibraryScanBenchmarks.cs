using BenchmarkDotNet.Attributes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Benchmarks.Helpers;
using Nagi.Core.Data;
using Nagi.Core.Helpers;
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

    [Params(100, 500)] // Using smaller numbers for quicker CI, can be increased locally
    public int SongCount;

    [GlobalSetup]
    public void Setup()
    {
        _testPath = Path.Combine(Path.GetTempPath(), "NagiBenchmarks", Guid.NewGuid().ToString());
        SyntheticAudioGenerator.GenerateLibrary(_testPath, SongCount);

        var services = new ServiceCollection();
        
        // Use In-Memory SQLite for benchmarks to isolate file scanning + business logic performance
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
        services.AddSingleton<ISpotifyService>(Substitute.For<ISpotifyService>());
        services.AddSingleton<ISettingsService>(Substitute.For<ISettingsService>());
        services.AddSingleton<IReplayGainService>(Substitute.For<IReplayGainService>());
        services.AddSingleton<IMetadataService, AtlMetadataService>();
        services.AddHttpClient();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));

        _serviceProvider = services.BuildServiceProvider();
        
        // Ensure database is created and migrated
        using var scope = _serviceProvider.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MusicDbContext>>();
        using var context = factory.CreateDbContext();
        context.Database.EnsureCreated();

        _libraryService = ActivatorUtilities.CreateInstance<LibraryService>(_serviceProvider);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (_serviceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }
        
        // Ensure SQLite releases all file locks
        SqliteConnection.ClearAllPools();
        
        // Give the OS a moment to release handles if needed, or just retry
        if (Directory.Exists(_testPath))
        {
            try 
            {
                Directory.Delete(_testPath, true);
            }
            catch (IOException)
            {
                // Fallback: Try again after a short delay or ignore if it's just temp files
                Thread.Sleep(100);
                if (Directory.Exists(_testPath))
                {
                    try { Directory.Delete(_testPath, true); } catch { /* Ignore */ }
                }
            }
        }
    }

    [Benchmark]
    public async Task InitialScan()
    {
        // Reset DB for clean scan
        using var scope = _serviceProvider.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MusicDbContext>>();
        using var context = factory.CreateDbContext();
        await context.Songs.ExecuteDeleteAsync();
        await context.Albums.ExecuteDeleteAsync();
        await context.Artists.ExecuteDeleteAsync();

        await _libraryService.ScanFolderForMusicAsync(_testPath);
    }

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
    }
}

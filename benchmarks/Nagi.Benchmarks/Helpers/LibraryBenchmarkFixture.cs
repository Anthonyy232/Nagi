using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Data;
using Nagi.Core.Helpers;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
using NSubstitute;

namespace Nagi.Benchmarks.Helpers;

internal sealed class LibraryBenchmarkFixture : IDisposable
{
    private readonly ServiceProvider _services;

    public string RootPath { get; } = Directory.CreateTempSubdirectory("NagiBenchmarks-").FullName;
    public IDbContextFactory<MusicDbContext> ContextFactory { get; }
    public LibraryReader Reader { get; }
    public LibraryService Library { get; }
    public SmartPlaylistService SmartPlaylists { get; }

    public LibraryBenchmarkFixture()
    {
        var services = new ServiceCollection();

        var dbPath = Path.Combine(RootPath, "test.db");
        services.AddDbContextFactory<MusicDbContext>(options =>
            options.UseSqlite(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString()));

        services.AddSingleton<IFileSystemService, TestFileSystemService>();
        services.AddSingleton<IPathConfiguration>(_ => {
            var pathConfig = Substitute.For<IPathConfiguration>();
            pathConfig.AlbumArtCachePath.Returns(Path.Combine(RootPath, "Art"));
            pathConfig.ArtistImageCachePath.Returns(Path.Combine(RootPath, "Artists"));
            pathConfig.LrcCachePath.Returns(Path.Combine(RootPath, "Lrc"));
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
        services.AddSingleton<IProviderPipelineProvider>(Substitute.For<IProviderPipelineProvider>());
        services.AddSingleton<IMetadataService, AtlMetadataService>();
        services.AddSingleton<ILibraryReader, LibraryReader>();
        services.AddHttpClient();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));

        _services = services.BuildServiceProvider();

        ContextFactory = _services.GetRequiredService<IDbContextFactory<MusicDbContext>>();
        using var context = ContextFactory.CreateDbContext();
        context.Database.EnsureCreated();

        Reader = (LibraryReader)_services.GetRequiredService<ILibraryReader>();
        Library = ActivatorUtilities.CreateInstance<LibraryService>(_services);
        SmartPlaylists = ActivatorUtilities.CreateInstance<SmartPlaylistService>(_services);
    }

    public void Dispose()
    {
        Library.Dispose();
        _services.Dispose();
        Directory.Delete(RootPath, recursive: true);
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
        public void MoveFile(string source, string dest, bool overwrite) => File.Move(source, dest, overwrite);
        public FileInfo GetFileInfo(string path) => new FileInfo(path);
        public bool IsHiddenOrSystemFile(string path)
        {
            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
        }
        public string NormalizePath(string path) => PathCanonicalizer.Normalize(path);
        public bool IsNetworkPath(string path) => false;
    }
}

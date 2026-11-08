using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Artwork;
using Nagi.Core.Data;
using Nagi.Core.Library;
using Nagi.Core.Listening;
using Nagi.Core.Metadata;
using Nagi.Core.MusicServers;
using Nagi.Core.OnlineProviders.Pipelines;
using Nagi.Core.Platform;
using Nagi.Core.Playback;
using Nagi.Core.Playlists;
using Nagi.Core.Settings;
using NSubstitute;

namespace Nagi.Core.Tests.Utils;

/// <summary>
///     The library services wired together as the app wires them, over a test database and the given collaborators.
/// </summary>
public sealed class TestLibrary : IDisposable
{
    public TestLibrary(
        IDbContextFactory<MusicDbContext> contextFactory,
        IFileSystemService fileSystem,
        IMetadataService metadataService,
        IArtistMetadataService artistMetadataService,
        IAlbumCoverService albumCoverService,
        IHttpClientFactory httpClientFactory,
        IPathConfiguration pathConfig,
        ISettingsService settingsService,
        IReplayGainService replayGainService,
        IImageProcessor imageProcessor,
        ProviderPipelineProvider pipelines)
    {
        WriteLock = new LibraryWriteLock(NullLogger<LibraryWriteLock>.Instance);
        Reader = new LibraryReader(contextFactory, fileSystem, settingsService);
        var libraryArtwork = new LibraryArtwork(contextFactory, fileSystem, pathConfig, imageProcessor, Notifier,
            NullLogger<LibraryArtwork>.Instance);
        Artwork = new ArtworkService(contextFactory, fileSystem, pathConfig, imageProcessor, artistMetadataService,
            albumCoverService, settingsService, httpClientFactory, pipelines, libraryArtwork, Notifier, WriteLock,
            NullLogger<ArtworkService>.Instance);
        Importer = new LibraryImporter(contextFactory, fileSystem, pathConfig, libraryArtwork, Notifier, settingsService,
            NullLogger<LibraryImporter>.Instance);
        Folders = new LibraryFolderService(contextFactory, fileSystem, metadataService, imageProcessor, replayGainService,
            settingsService, Importer, Artwork, libraryArtwork, Notifier, WriteLock, NullLogger<LibraryFolderService>.Instance);
        Listening = new ListeningService(contextFactory, Notifier, NullLogger<ListeningService>.Instance);
        Playlists = new PlaylistService(contextFactory, fileSystem, imageProcessor, pathConfig, Notifier,
            NullLogger<PlaylistService>.Instance);
        Servers = new ServerLibrarySync(contextFactory, fileSystem, imageProcessor, settingsService, Importer, Artwork,
            libraryArtwork, Playlists, Notifier, WriteLock, NullLogger<ServerLibrarySync>.Instance);
        MusicServers = Substitute.For<IMusicServerService>();
        Reset = new LibraryReset(contextFactory, fileSystem, pathConfig, MusicServers,
            Artwork, Notifier, WriteLock, NullLogger<LibraryReset>.Instance);
    }

    public LibraryNotifier Notifier { get; } = new();
    public LibraryWriteLock WriteLock { get; }
    public LibraryReader Reader { get; }
    public ArtworkService Artwork { get; }
    public LibraryImporter Importer { get; }
    public LibraryFolderService Folders { get; }
    public ListeningService Listening { get; }
    public PlaylistService Playlists { get; }
    public ServerLibrarySync Servers { get; }
    public LibraryReset Reset { get; }
    public IMusicServerService MusicServers { get; }

    public void Dispose() => WriteLock.Dispose();
}

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nagi.Core.Data;
using Nagi.Core.Helpers;
using Nagi.Core.Http;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations.MusicServers;
using Nagi.Core.Services.Implementations.Presence;
using Nagi.Core.Services.Implementations.Romanization;
using Nagi.Core.Services.Implementations;

namespace Nagi.Core.Services;

/// <summary>
///     Registers Nagi's platform-independent services.
/// </summary>
public static class CoreServiceRegistration
{
    /// <summary>
    ///     Adds the library, playback, metadata, scrobbling, lyrics, and maintenance services.
    ///     The host must also register logging, <c>IConfiguration</c>, and its platform services:
    ///     <see cref="IPathConfiguration" />, <see cref="ISettingsService" />, <see cref="IAppInfoService" />, <see cref="ICredentialLockerService" />, <see cref="IAudioPlayer" />
    ///     and <see cref="ISystemMediaControls" />.
    /// </summary>
    public static IServiceCollection AddNagiCore(this IServiceCollection services)
    {
        services.AddDbContextFactory<MusicDbContext>((serviceProvider, options) =>
        {
            var pathConfig = serviceProvider.GetRequiredService<IPathConfiguration>();
            options.UseSqlite(new SqliteConnectionStringBuilder { DataSource = pathConfig.DatabasePath }.ToString());
        });

        services.AddSingleton<IFileSystemService, FileSystemService>();
        services.AddSingleton<ILibraryReader, LibraryReader>();
        services.AddSingleton<IPlaylistService, PlaylistService>();
        services.AddSingleton<LibraryService>();
        services.AddSingleton<ILibraryWriter>(sp => sp.GetRequiredService<LibraryService>());
        services.AddSingleton<ILibraryScanner>(sp => sp.GetRequiredService<LibraryService>());
        services.AddSingleton<LibraryFolderWatcher>();

        services.AddSingleton<IMusicPlaybackService, MusicPlaybackService>();
        services.AddSingleton<OfflineScrobbleService>();
        services.AddSingleton<IImageProcessor, ImageSharpProcessor>();
        services.AddSingleton<IMetadataService, AtlMetadataService>();
        services.AddSingleton<ILrcService, LrcService>();

        services.AddSingleton<IApiKeyService, ApiKeyService>();

        services.AddSingleton(sp => new ProviderPipelineProvider(ProviderCatalog.Policies,
            sp.GetRequiredService<ILogger<ProviderPipelineProvider>>()));

        services.AddSingleton<ILastFmMetadataService, LastFmMetadataService>();
        services.AddSingleton<ILastFmAuthService, LastFmAuthService>();
        services.AddSingleton<IMusicBrainzService, MusicBrainzService>();
        services.AddSingleton<IFanartTvService, FanartTvService>();
        services.AddSingleton<ITheAudioDbService, TheAudioDbService>();
        services.AddSingleton<IDeezerService, DeezerService>();
        services.AddSingleton<IWikipediaService, WikipediaService>();
        services.AddSingleton<ICoverArtArchiveService, CoverArtArchiveService>();
        services.AddSingleton<IAppleMusicService, AppleMusicService>();
        services.AddSingleton<IArtistMetadataService, ArtistMetadataService>();
        services.AddSingleton<IAlbumCoverService, AlbumCoverService>();
        services.AddSingleton<INetEaseLyricsService, NetEaseLyricsService>();
        services.AddSingleton<LastFmScrobblerService>();
        services.AddSingleton<ILastFmScrobblerService>(sp => sp.GetRequiredService<LastFmScrobblerService>());
        services.AddSingleton<IListenSubmitter>(sp => sp.GetRequiredService<LastFmScrobblerService>());
        services.AddSingleton<ListenBrainzScrobblerService>();
        services.AddSingleton<IListenBrainzScrobblerService>(sp => sp.GetRequiredService<ListenBrainzScrobblerService>());
        services.AddSingleton<IListenSubmitter>(sp => sp.GetRequiredService<ListenBrainzScrobblerService>());
        services.AddSingleton<PresenceManager>();
        services.AddSingleton<IPresenceService, DiscordPresenceService>();
        services.AddSingleton<IPresenceService, LastFmPresenceService>();
        services.AddSingleton<IPresenceService, ListenBrainzPresenceService>();
        services.AddSingleton<IMusicServerService, MusicServerService>();
        services.AddSingleton<IPresenceService, MusicServerPresenceService>();
        services.AddSingleton<ISmartPlaylistService, SmartPlaylistService>();
        services.AddSingleton<IOnlineLyricsService, LrcLibService>();
        services.AddSingleton<IRomanizationProvider, JapaneseRomanizationProvider>();
        services.AddSingleton<IRomanizationProvider, DevanagariRomanizationProvider>();
        services.AddSingleton<IRomanizationProvider, KoreanRomanizationProvider>();
        services.AddSingleton<IRomanizationCatalogVerifier, EcdsaRomanizationCatalogVerifier>();
        services.AddSingleton<IRomanizationPackManager, RomanizationPackManager>();
        services.AddSingleton<ILyricRomanizationService, LyricRomanizationService>();
        services.AddSingleton<IPlaylistExportService, M3uPlaylistExportService>();
        services.AddSingleton<IBackupRestoreService, BackupRestoreService>();
        services.AddSingleton<IStatisticsService, StatisticsService>();

        services.AddSingleton<IFFmpegService, FFmpegService>();
        services.AddSingleton<ILoudnessAnalyzer, FFmpegLoudnessAnalyzer>();
        services.AddSingleton<IReplayGainService, ReplayGainService>();

        AddHttpClients(services);
        return services;
    }

    private static void AddHttpClients(IServiceCollection services)
    {
        services.ConfigureHttpClientDefaults(builder =>
            builder.ConfigureHttpClient(client => client.DefaultRequestHeaders.UserAgent.ParseAdd(ProviderUserAgent.Value)));
        services.AddHttpClient("")
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(20))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                ConnectTimeout = TimeSpan.FromSeconds(10),
                KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
                KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(5)
            });

        // A sync makes thousands of requests, which would crowd everything else out of the log kept for crash reports.
        services.AddHttpClient(MusicServerService.HttpClientName)
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                ConnectTimeout = TimeSpan.FromSeconds(10),
            })
            .RemoveAllLoggers();

        // Image downloads are larger payloads and tolerate slower CDNs; use a longer timeout.
        services.AddHttpClient("ImageDownloader")
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                ConnectTimeout = TimeSpan.FromSeconds(10),
            });
    }
}

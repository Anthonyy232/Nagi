using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nagi.Core.Data;
using Nagi.Core.Helpers;
using Nagi.Core.Http;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Library;
using Nagi.Core.Playback;
using Nagi.Core.Playlists;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Services.Implementations.MusicServers;
using Nagi.Core.Services.Implementations.Presence;
using Nagi.Core.Services.Implementations.Romanization;
using Nagi.Core.Settings;

namespace Nagi.Core.Services;

/// <summary>
///     Registers Nagi's platform-independent services.
/// </summary>
public static class CoreServiceRegistration
{
    /// <summary>
    ///     Adds the library, playback, metadata, scrobbling, lyrics, and maintenance services.
    ///     The host must also register logging, <c>IConfiguration</c>, and its platform services:
    ///     <see cref="IPathConfiguration" />, <see cref="ISettingsStore" />, <see cref="IAppInfoService" />, <see cref="ICredentialLockerService" />,
    ///     <see cref="IAutoLaunchService" />, <see cref="IAudioPlayer" />, <see cref="ISystemMediaControls" /> and
    ///     <see cref="IDispatcherService" />.
    /// </summary>
    public static IServiceCollection AddNagiCore(this IServiceCollection services)
    {
        services.AddDbContextFactory<MusicDbContext>((serviceProvider, options) =>
        {
            var pathConfig = serviceProvider.GetRequiredService<IPathConfiguration>();
            options.UseSqlite(new SqliteConnectionStringBuilder { DataSource = pathConfig.DatabasePath }.ToString());
        });

        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSettings(typeof(CoreSettings));
        services.AddSingleton<IFileSystemService, FileSystemService>();
        services.AddSingleton<ILibraryReader, LibraryReader>();
        services.AddSingleton<PlaylistService>();
        services.AddSingleton<IPlaylistService>(sp => sp.GetRequiredService<PlaylistService>());
        services.AddSingleton<LibraryNotifier>();
        services.AddSingleton<ILibraryNotifier>(sp => sp.GetRequiredService<LibraryNotifier>());
        services.AddSingleton<LibraryWriteLock>();
        services.AddSingleton<ArtworkService>();
        services.AddSingleton<IArtworkService>(sp => sp.GetRequiredService<ArtworkService>());
        services.AddSingleton<LibraryImporter>();
        services.AddSingleton<ILibraryFolderService, LibraryFolderService>();
        services.AddSingleton<ILibraryReset, LibraryReset>();
        services.AddSingleton<IListeningService, ListeningService>();
        services.AddSingleton<IServerLibrarySync, ServerLibrarySync>();
        services.AddSingleton<LibraryFolderWatcher>();

        services.AddSingleton<IPlaybackStateStore, PlaybackStateStore>();
        services.AddSingleton<IMusicPlaybackService, MusicPlaybackService>();
        services.AddSingleton<IEqualizerService, EqualizerService>();
        services.AddSingleton<ScrobbleCoordinator>();
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
        services.AddSingleton<LastFmScrobblerService>();
        services.AddSingleton<IListenSubmitter>(sp => sp.GetRequiredService<LastFmScrobblerService>());
        services.AddSingleton<ListenBrainzScrobblerService>();
        services.AddSingleton<IListenBrainzScrobblerService>(sp => sp.GetRequiredService<ListenBrainzScrobblerService>());
        services.AddSingleton<IScrobblingAccounts, ScrobblingAccounts>();
        services.AddSingleton<IListenSubmitter>(sp => sp.GetRequiredService<ListenBrainzScrobblerService>());
        services.AddSingleton<PresenceManager>();
        services.AddSingleton<IPresenceService, DiscordPresenceService>();
        services.AddSingleton<MusicServerService>();
        services.AddSingleton<IMusicServerService>(sp => sp.GetRequiredService<MusicServerService>());
        services.AddSingleton<IListenSubmitter>(sp => sp.GetRequiredService<MusicServerService>());
        services.AddSingleton<ISmartPlaylistService, SmartPlaylistService>();
        services.AddSingleton<ILyricsProvider, LrcLibService>();
        services.AddSingleton<ILyricsProvider, NetEaseLyricsService>();
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

    /// <summary>
    ///     Adds the settings declared in <paramref name="declaringType" /> to the ones the settings service resets and backs up.
    /// </summary>
    public static IServiceCollection AddSettings(this IServiceCollection services, Type declaringType)
    {
        foreach (var setting in Setting.DeclaredIn(declaringType)) services.AddSingleton(setting);
        return services;
    }

    private static void AddHttpClients(IServiceCollection services)
    {
        services.ConfigureHttpClientDefaults(builder =>
            builder.ConfigureHttpClient(client => client.DefaultRequestHeaders.UserAgent.ParseAdd(ProviderUserAgent.Value)));
        services.AddHttpClient("")
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(20))
            .ConfigurePrimaryHttpMessageHandler(() =>
            {
                var handler = CreateHandler();
                handler.KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always;
                handler.KeepAlivePingDelay = TimeSpan.FromSeconds(30);
                handler.KeepAlivePingTimeout = TimeSpan.FromSeconds(5);
                return handler;
            });

        // A sync makes thousands of requests, which would crowd everything else out of the log kept for crash reports.
        services.AddHttpClient(MusicServerService.HttpClientName)
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(CreateHandler)
            .RemoveAllLoggers();

        // Image downloads are larger payloads and tolerate slower CDNs; use a longer timeout.
        services.AddHttpClient(ArtworkService.ImageHttpClientName)
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(CreateHandler);
    }

    // Clients live as long as the app, so pooled connections are recycled to pick up DNS changes.
    private static SocketsHttpHandler CreateHandler() => new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectTimeout = TimeSpan.FromSeconds(10)
    };
}

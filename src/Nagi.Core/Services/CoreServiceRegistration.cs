using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nagi.Core.Data;
using Nagi.Core.Helpers;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
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
    ///     The host must also register logging, HTTP clients, <c>IConfiguration</c>, and its platform services:
    ///     <see cref="IPathConfiguration" />, <see cref="ISettingsService" />, <see cref="IAudioPlayer" />
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
        services.AddSingleton<ILibraryFolderWatcher, LibraryFolderWatcher>();

        services.AddSingleton<IMusicPlaybackService, MusicPlaybackService>();
        services.AddSingleton<IOfflineScrobbleService, OfflineScrobbleService>();
        services.AddSingleton<IImageProcessor, ImageSharpProcessor>();
        services.AddSingleton<IMetadataService, AtlMetadataService>();
        services.AddSingleton<ILrcService, LrcService>();

        services.AddSingleton<IApiKeyService, ApiKeyService>();

        services.AddProviderPipelines(
        [
            new ProviderPolicy
            {
                ProviderId = ServiceProviderIds.MusicBrainz,
                // MusicBrainz mandates 1 req/s per IP with a UA identifying the client.
                // Excess traffic returns 503 until the rate falls back under 1 RPS.
                Channel = new ChannelPolicy
                {
                    PermitsPerWindow = 1,
                    MaxConcurrent = 1,
                    BaseRetryDelay = TimeSpan.FromSeconds(1),
                },
            },

            new ProviderPolicy
            {
                ProviderId = ServiceProviderIds.TheAudioDb,
                // Free tier: 30 req/min. Once breached, the key is locked out for a full
                // minute — short retry delays just waste calls. Wait the full minute, once.
                Channel = new ChannelPolicy
                {
                    PermitsPerWindow = 1,
                    Window = TimeSpan.FromSeconds(2),
                    MaxConcurrent = 2,
                    MaxRetries = 1,
                    BaseRetryDelay = TimeSpan.FromSeconds(60),
                    MaxRetryDelay = TimeSpan.FromSeconds(75),
                },
            },

            // Last.fm: metadata, auth, and scrobbling all run through a single provider so an
            // auth-trip on one of them pauses the others (shared breaker). Last.fm's limits
            // are undisclosed/dynamic; 5 RPS/IP is the long-standing convention.
            new ProviderPolicy
            {
                ProviderId = ServiceProviderIds.LastFm,
                Channel = new ChannelPolicy
                {
                    PermitsPerWindow = 5,
                    MaxConcurrent = 4,
                    BaseRetryDelay = TimeSpan.FromSeconds(5),
                },
            },

            // Fanart.tv: effectively unlimited for normal use (token bucket, 429 rare). The
            // pipeline auto-honors Retry-After if the service ever pushes back.
            new ProviderPolicy
            {
                ProviderId = ServiceProviderIds.FanartTv,
                Channel = new ChannelPolicy
                {
                    PermitsPerWindow = 10,
                    MaxConcurrent = 8,
                },
            },

            // LRCLIB: no rate limit documented. Keep concurrency reasonable to be polite.
            new ProviderPolicy
            {
                ProviderId = ServiceProviderIds.LrcLib,
                Channel = new ChannelPolicy
                {
                    PermitsPerWindow = 20,
                    MaxConcurrent = 8,
                },
            },

            // NetEase: unofficial API; treat aggressively-rate-limited and back off fast.
            new ProviderPolicy
            {
                ProviderId = ServiceProviderIds.NetEase,
                Channel = new ChannelPolicy
                {
                    PermitsPerWindow = 2,
                    MaxConcurrent = 2,
                    BaseRetryDelay = TimeSpan.FromSeconds(3),
                },
            },

            // ListenBrainz: dynamic limit advertised via X-RateLimit-* headers. The pipeline
            // honors X-RateLimit-Reset-In on 429 (alongside Retry-After), so we can run at a
            // healthy steady-state and let the server tell us when to back off.
            new ProviderPolicy
            {
                ProviderId = ServiceProviderIds.ListenBrainz,
                Channel = new ChannelPolicy
                {
                    PermitsPerWindow = 5,
                    MaxConcurrent = 4,
                },
            },

            // Nagi's own API server (key bootstrap). Internal infra; conservative retry.
            new ProviderPolicy
            {
                ProviderId = ServiceProviderIds.NagiApi,
                Channel = new ChannelPolicy
                {
                    PermitsPerWindow = 5,
                    MaxConcurrent = 4,
                },
            },

            // Generic CDN image downloads. Provider id is shared by all artist/album image
            // fetches because the source CDN isn't always knowable from the URL.
            new ProviderPolicy
            {
                ProviderId = ServiceProviderIds.ImageDownload,
                Channel = new ChannelPolicy
                {
                    PermitsPerWindow = 10,
                    MaxConcurrent = 4,
                },
            },
        ]);

        services.AddSingleton<ILastFmMetadataService, LastFmMetadataService>();
        services.AddSingleton<ILastFmAuthService, LastFmAuthService>();
        services.AddSingleton<IMusicBrainzService, MusicBrainzService>();
        services.AddSingleton<IFanartTvService, FanartTvService>();
        services.AddSingleton<ITheAudioDbService, TheAudioDbService>();
        services.AddSingleton<INetEaseLyricsService, NetEaseLyricsService>();
        services.AddSingleton<LastFmScrobblerService>();
        services.AddSingleton<ILastFmScrobblerService>(sp => sp.GetRequiredService<LastFmScrobblerService>());
        services.AddSingleton<IListenSubmitter>(sp => sp.GetRequiredService<LastFmScrobblerService>());
        services.AddSingleton<ListenBrainzScrobblerService>();
        services.AddSingleton<IListenBrainzScrobblerService>(sp => sp.GetRequiredService<ListenBrainzScrobblerService>());
        services.AddSingleton<IListenSubmitter>(sp => sp.GetRequiredService<ListenBrainzScrobblerService>());
        services.AddSingleton<IPresenceManager, PresenceManager>();
        services.AddSingleton<IAlbumArtUrlResolver, AlbumArtUrlResolver>();
        services.AddSingleton<IPresenceService, DiscordPresenceService>();
        services.AddSingleton<IPresenceService, LastFmPresenceService>();
        services.AddSingleton<IPresenceService, ListenBrainzPresenceService>();
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

        return services;
    }
}

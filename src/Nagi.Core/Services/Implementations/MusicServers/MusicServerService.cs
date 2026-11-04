using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Data;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations.MusicServers;

/// <summary>
///     Keeps the library in step with the user's music servers. The library writes and scans live in
///     <see cref="LibraryService" />; this service owns the connections and the server's side of favorites and plays.
/// </summary>
public sealed class MusicServerService : IMusicServerService, IDisposable
{
    internal const string HttpClientName = "MusicServer";
    private const string CredentialResourcePrefix = "Nagi/MusicServer/";
    private static readonly TimeSpan PeriodicSyncInterval = TimeSpan.FromMinutes(30);

    private readonly IDbContextFactory<MusicDbContext> _contextFactory;
    private readonly ILibraryWriter _libraryWriter;
    private readonly ILibraryScanner _libraryScanner;
    private readonly ICredentialLockerService _credentials;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAppInfoService _appInfo;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MusicServerService> _logger;
    private readonly ConcurrentDictionary<Guid, IMusicServerClient> _clients = new();
    // One push at a time, so an older favorite can't reach the server after a newer one.
    private readonly SemaphoreSlim _favoritesLock = new(1, 1);
    // Plays are reported live and before each sync; one at a time keeps a play from being counted twice.
    private readonly SemaphoreSlim _playsLock = new(1, 1);
    private readonly CancellationTokenSource _shutdownCts = new();

    public MusicServerService(
        IDbContextFactory<MusicDbContext> contextFactory,
        ILibraryWriter libraryWriter,
        ILibraryScanner libraryScanner,
        ICredentialLockerService credentials,
        IHttpClientFactory httpClientFactory,
        IAppInfoService appInfo,
        ILogger<MusicServerService> logger,
        TimeProvider? timeProvider = null)
    {
        _contextFactory = contextFactory;
        _libraryWriter = libraryWriter;
        _libraryScanner = libraryScanner;
        _credentials = credentials;
        _httpClientFactory = httpClientFactory;
        _appInfo = appInfo;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _libraryScanner.LibraryContentChanged += OnLibraryContentChanged;
    }

    public event Action<Guid>? SignInRejected;

    public Task<Folder> AddAsync(MusicServerKind kind, string address, string userName, string password) =>
        AddCoreAsync(kind, address, userName.Trim(), password, false);

    public Task<Folder> AddWithApiKeyAsync(string address, string apiKey) =>
        AddCoreAsync(MusicServerKind.OpenSubsonic, address, string.Empty, apiKey.Trim(), true);

    public async Task SignInAsync(Guid folderId, string password)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var server = await context.MusicServers.FirstOrDefaultAsync(s => s.FolderId == folderId).ConfigureAwait(false)
                     ?? throw new MusicServerException(MusicServerError.NotFound, $"Server folder {folderId} is no longer in the library.");

        var userName = server.UserName;
        var (_, secret) = await ConnectAsync(server, server.UsesApiKey ? password.Trim() : password).ConfigureAwait(false);
        // An API key names its own user; the library's favorites and plays must stay with the account it was added as.
        if (!string.Equals(server.UserName, userName, StringComparison.OrdinalIgnoreCase))
            throw new MusicServerException(MusicServerError.DifferentAccount, $"The API key is for {server.UserName}, not {userName}.");
        if (!_credentials.SaveCredential(CredentialResource(folderId), server.UserName, secret))
            throw new MusicServerException(MusicServerError.SignInNotSaved, $"The sign-in for {server.BaseUrl} couldn't be saved.");
        await context.SaveChangesAsync().ConfigureAwait(false);
        // The cached client still holds the sign-in the server stopped accepting.
        _clients.TryRemove(folderId, out _);
        _logger.LogInformation("Signed in to server folder {FolderId} again.", folderId);
    }

    public async Task<bool> RemoveAsync(Guid folderId)
    {
        // Read first: removing the server also removes the address and sign-in needed to sign out.
        IMusicServerClient? client = null;
        try
        {
            client = await GetClientAsync(folderId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (MusicServerException ex)
        {
            _logger.LogDebug(ex, "Server folder {FolderId} has no sign-in to end.", folderId);
        }

        if (!await _libraryWriter.RemoveFolderAsync(folderId).ConfigureAwait(false)) return false;
        // In the background, so a server that can't be reached doesn't hold up its removal.
        if (client is not null) _ = SignOutAsync(folderId, client);
        return true;
    }

    public async Task<MusicServer?> GetServerAsync(Guid folderId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.MusicServers.AsNoTracking().Include(s => s.Folder)
            .FirstOrDefaultAsync(s => s.FolderId == folderId).ConfigureAwait(false);
    }

    public async Task SyncAsync(Guid folderId, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var syncCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        var client = await GetClientAsync(folderId, syncCts.Token).ConfigureAwait(false);
        if (client is null) return;

        // The server must hear about Nagi's favorites and plays before the sync reads its own back.
        await PushPendingFavoritesAsync(folderId, syncCts.Token).ConfigureAwait(false);
        await ReportPlaysAsync(folderId, syncCts.Token).ConfigureAwait(false);
        await _libraryScanner.SyncServerFolderAsync(folderId, client, progress, syncCts.Token).ConfigureAwait(false);
    }

    public async Task SyncAllAsync(IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        List<Guid> folderIds;
        await using (var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
            folderIds = await context.MusicServers.Select(s => s.FolderId).ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var folderId in folderIds)
        {
            try
            {
                await SyncAsync(folderId, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_shutdownCts.IsCancellationRequested)
            {
                _logger.LogDebug("Server folder {FolderId} was removed while it synced.", folderId);
            }
            catch (MusicServerException ex) when (ex.Error == MusicServerError.SignInFailed)
            {
                _logger.LogWarning(ex, "Server folder {FolderId} rejected the saved sign-in.", folderId);
                SignInRejected?.Invoke(folderId);
            }
            // One server failing mustn't keep the others from syncing.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Couldn't sync server folder {FolderId}.", folderId);
            }
        }
    }

    // In the background, so a slow or unreachable server can't hold up startup.
    public void StartPeriodicSync() => _ = Task.Run(() => SyncPeriodicallyAsync(_shutdownCts.Token));

    public async Task ReportPlaysAsync(Guid folderId, CancellationToken cancellationToken = default)
    {
        await _playsLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
            var pending = await context.ListenHistory
                .Where(lh => lh.Song.FolderId == folderId && lh.IsEligibleForScrobbling && !lh.IsSubmittedToServer)
                .OrderBy(lh => lh.ListenTimestampUtc)
                .Select(lh => new { lh.Id, lh.SongId, RemoteId = lh.Song.RemoteId!, lh.ListenTimestampUtc })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (pending.Count == 0) return;

            var client = await GetClientAsync(folderId, cancellationToken).ConfigureAwait(false);
            if (client is null) return;
            foreach (var play in pending)
            {
                try
                {
                    await client.ReportPlayedAsync(play.RemoteId, play.ListenTimestampUtc, cancellationToken).ConfigureAwait(false);
                }
                catch (MusicServerException ex) when (ex.Error == MusicServerError.NotFound)
                {
                    _logger.LogWarning("Dropping a play of song {SongId}: it is no longer on its server.", play.SongId);
                }

                // Marked at once, so a play already counted isn't sent again if a later one fails or shutdown cancels.
                await context.ListenHistory.Where(lh => lh.Id == play.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(lh => lh.IsSubmittedToServer, true), CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (MusicServerException ex)
        {
            // The rest stay queued in order; the next attempt starts from the play that failed.
            _logger.LogWarning(ex, "Couldn't report plays to server folder {FolderId}; will retry.", folderId);
        }
        finally
        {
            _playsLock.Release();
        }
    }

    public async Task<Uri?> GetStreamUriAsync(Song song)
    {
        if (!song.IsRemote) return null;
        try
        {
            var client = await GetClientAsync(song.FolderId, CancellationToken.None).ConfigureAwait(false);
            return client?.GetStreamUri(song.RemoteId!);
        }
        catch (MusicServerException ex)
        {
            _logger.LogWarning(ex, "Can't stream song {SongId}.", song.Id);
            return null;
        }
    }

    public async Task CheckSongAsync(Song song, CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync(song.FolderId, cancellationToken).ConfigureAwait(false)
                     ?? throw new MusicServerException(MusicServerError.NotFound, $"Server folder {song.FolderId} is no longer in the library.");
        await client.CheckSongAsync(song.RemoteId!, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetLyricsAsync(Song song, CancellationToken cancellationToken = default)
    {
        if (!song.IsRemote) return null;
        try
        {
            var client = await GetClientAsync(song.FolderId, cancellationToken).ConfigureAwait(false);
            return client is null ? null : await client.GetLyricsAsync(song.RemoteId!, cancellationToken).ConfigureAwait(false);
        }
        // A server without the lyrics extension rejects the request every time, so it has no lyrics to give.
        catch (MusicServerException ex) when (ex.Error is MusicServerError.UnsupportedServer or MusicServerError.ServerError
                                                  or MusicServerError.NotFound)
        {
            _logger.LogDebug(ex, "Couldn't get lyrics for song {SongId} from its server.", song.Id);
            return null;
        }
    }

    public void Dispose()
    {
        if (_shutdownCts.IsCancellationRequested) return;
        _libraryScanner.LibraryContentChanged -= OnLibraryContentChanged;
        _shutdownCts.Cancel();
        _shutdownCts.Dispose();
    }

    private async Task SyncPeriodicallyAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PeriodicSyncInterval, _timeProvider);
        try
        {
            do
            {
                try
                {
                    await SyncAllAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Syncing music servers failed.");
                }
            } while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // The app closed.
        }
    }

    private async Task<Folder> AddCoreAsync(MusicServerKind kind, string address, string userName, string secret,
        bool usesApiKey)
    {
        var server = new MusicServer
        {
            FolderId = Guid.NewGuid(),
            Kind = kind,
            BaseUrl = kind == MusicServerKind.OpenSubsonic
                ? OpenSubsonicClient.NormalizeAddress(address)
                : MusicServerHttp.NormalizeAddress(address),
            UserName = userName,
            UsesApiKey = usesApiKey
        };
        // Checked before signing in where possible, so adding a Jellyfin server twice doesn't leave a sign-in behind.
        if (!usesApiKey) await EnsureNotAddedAsync(server).ConfigureAwait(false);
        var (name, savedSecret) = await ConnectAsync(server, secret).ConfigureAwait(false);
        if (usesApiKey) await EnsureNotAddedAsync(server).ConfigureAwait(false);
        name = await GetUniqueNameAsync(name, server).ConfigureAwait(false);

        var resource = CredentialResource(server.FolderId);
        if (!_credentials.SaveCredential(resource, server.UserName, savedSecret))
            throw new MusicServerException(MusicServerError.SignInNotSaved, $"The sign-in for {server.BaseUrl} couldn't be saved.");
        try
        {
            var folder = await _libraryWriter.AddMusicServerAsync(server, name).ConfigureAwait(false);
            _logger.LogInformation("Added {Kind} server {BaseUrl} as folder {FolderId}.", kind, server.BaseUrl, folder.Id);
            return folder;
        }
        catch
        {
            _credentials.RemoveCredential(resource);
            throw;
        }
    }

    /// <summary>
    ///     Signs in to the server, filling in the user it knows the sign-in as.
    /// </summary>
    /// <returns>The server's name, and the secret to keep: the password or API key, or Jellyfin's access token.</returns>
    private async Task<(string Name, string Secret)> ConnectAsync(MusicServer server, string secret)
    {
        var httpClient = _httpClientFactory.CreateClient(HttpClientName);
        switch (server.Kind)
        {
            case MusicServerKind.OpenSubsonic:
                (var name, server.UserName) = await OpenSubsonicClient.ConnectAsync(httpClient, server.BaseUrl,
                    server.UserName, secret, server.UsesApiKey, _appInfo.GetAppName()).ConfigureAwait(false);
                return (name, secret);
            case MusicServerKind.Jellyfin:
                var signIn = await JellyfinClient.SignInAsync(httpClient, server.BaseUrl, server.UserName, secret,
                    CreateIdentity(server.FolderId)).ConfigureAwait(false);
                server.UserId = signIn.UserId;
                // Jellyfin's access token replaces the password, which isn't kept.
                return (signIn.ServerName, signIn.AccessToken);
            default:
                throw new ArgumentOutOfRangeException(nameof(server), server.Kind, null);
        }
    }

    // Servers of the same kind would otherwise share a name, such as two called "Navidrome".
    private async Task<string> GetUniqueNameAsync(string name, MusicServer server)
    {
        var host = new Uri(server.BaseUrl).Authority;
        string[] candidates = [name, $"{name} ({host})", $"{name} ({server.UserName}@{host})"];
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var taken = await context.Folders.Where(f => f.ParentFolderId == null && candidates.Contains(f.Name))
            .Select(f => f.Name).ToListAsync().ConfigureAwait(false);
        return candidates.FirstOrDefault(candidate => !taken.Contains(candidate)) ?? candidates[^1];
    }

    private async Task EnsureNotAddedAsync(MusicServer server)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        if (await context.MusicServers
                .AnyAsync(s => s.Kind == server.Kind && s.BaseUrl == server.BaseUrl && s.UserName == server.UserName)
                .ConfigureAwait(false))
            throw new MusicServerException(MusicServerError.AlreadyAdded, $"{server.UserName} on {server.BaseUrl} is already in the library.");
    }

    private async void OnLibraryContentChanged(object? sender, LibraryContentChangedEventArgs e)
    {
        try
        {
            if (e is { ChangeType: LibraryChangeType.FolderRemoved, FolderId: { } folderId })
                ForgetServer(folderId);
            // Favorites changed through the library writer are flagged pending and announced as a song update.
            else if (e.ChangeType == LibraryChangeType.SongsUpdated)
                await PushPendingFavoritesAsync(null, _shutdownCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update music servers after a library change.");
        }
    }

    private async Task SignOutAsync(Guid folderId, IMusicServerClient client)
    {
        try
        {
            await client.SignOutAsync(_shutdownCts.Token).ConfigureAwait(false);
        }
        // The server keeps the session until it expires or is ended on the server.
        catch (Exception ex) when (ex is MusicServerException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "Couldn't sign out of removed server folder {FolderId}.", folderId);
        }
    }

    // Does nothing for a folder on disk, which has no sign-in.
    private void ForgetServer(Guid folderId)
    {
        _clients.TryRemove(folderId, out _);
        _credentials.RemoveCredential(CredentialResource(folderId));
    }

    /// <summary>
    ///     Tells servers about favorites changed in Nagi. A server that can't be reached keeps its changes pending
    ///     for the next sync or favorite change.
    /// </summary>
    /// <param name="folderId">The one server to update, or null for all of them.</param>
    private async Task PushPendingFavoritesAsync(Guid? folderId, CancellationToken cancellationToken)
    {
        await _favoritesLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var pending = await context.Songs
                .Where(s => s.IsLovedSyncPending && (folderId == null || s.FolderId == folderId))
                .Select(s => new { s.Id, s.FolderId, RemoteId = s.RemoteId!, s.IsLoved })
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            foreach (var server in pending.GroupBy(song => song.FolderId))
            {
                try
                {
                    var client = await GetClientAsync(server.Key, cancellationToken).ConfigureAwait(false);
                    if (client is null) continue;
                    foreach (var change in server.GroupBy(song => song.IsLoved))
                    {
                        await client.SetFavoriteAsync(change.Select(song => song.RemoteId).ToList(), change.Key, cancellationToken)
                            .ConfigureAwait(false);
                        var ids = change.Select(song => song.Id).ToList();
                        // A favorite changed again while this was sent stays pending, so the newer value goes next.
                        await context.Songs.Where(s => ids.Contains(s.Id) && s.IsLoved == change.Key)
                            .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.IsLovedSyncPending, false), cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                catch (MusicServerException ex)
                {
                    _logger.LogWarning(ex, "Couldn't send favorites to server folder {FolderId}; will retry.", server.Key);
                }
            }
        }
        finally
        {
            _favoritesLock.Release();
        }
    }

    /// <returns>The server's client, or null if the folder isn't a server.</returns>
    private async Task<IMusicServerClient?> GetClientAsync(Guid folderId, CancellationToken cancellationToken)
    {
        if (_clients.TryGetValue(folderId, out var cached)) return cached;

        MusicServer? server;
        await using (var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
            server = await context.MusicServers.AsNoTracking().FirstOrDefaultAsync(s => s.FolderId == folderId, cancellationToken)
                .ConfigureAwait(false);
        if (server is null) return null;

        var secret = _credentials.RetrieveCredential(CredentialResource(folderId))?.Password
                     ?? throw new MusicServerException(MusicServerError.SignInFailed, $"No saved sign-in for {server.BaseUrl}.");
        var httpClient = _httpClientFactory.CreateClient(HttpClientName);
        IMusicServerClient client = server.Kind switch
        {
            MusicServerKind.OpenSubsonic => new OpenSubsonicClient(httpClient, server.BaseUrl, server.UserName, secret,
                server.UsesApiKey, _appInfo.GetAppName()),
            MusicServerKind.Jellyfin => new JellyfinClient(httpClient, server.BaseUrl, server.UserId ?? string.Empty, secret,
                CreateIdentity(folderId)),
            _ => throw new InvalidOperationException($"Unknown server kind {server.Kind}.")
        };
        return _clients.GetOrAdd(folderId, client);
    }

    private JellyfinClientIdentity CreateIdentity(Guid folderId) =>
        new(_appInfo.GetAppName(), _appInfo.GetAppVersion(), Environment.MachineName, folderId.ToString("N"));

    private static string CredentialResource(Guid folderId) => CredentialResourcePrefix + folderId.ToString("N");
}

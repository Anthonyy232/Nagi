using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;

namespace Nagi.Presentation.ViewModels;

/// <summary>
///     Settings section for Last.fm and ListenBrainz accounts and their submission toggles.
/// </summary>
public partial class ScrobblingSettingsViewModel : SettingsSectionViewModel
{
    private readonly IUISettingsService _settingsService;
    private readonly IUIService _uiService;
    private readonly ILastFmAuthService _lastFmAuthService;
    private readonly IListenBrainzScrobblerService _listenBrainzScrobblerService;
    private int _lastFmAuthVersion;
    private int _listenBrainzConnectionVersion;

    public ScrobblingSettingsViewModel(
        IUISettingsService settingsService,
        IUIService uiService,
        ILastFmAuthService lastFmAuthService,
        IListenBrainzScrobblerService listenBrainzScrobblerService,
        IDispatcherService dispatcherService,
        ILogger<ScrobblingSettingsViewModel> logger)
        : base(dispatcherService, logger)
    {
        _settingsService = settingsService;
        _uiService = uiService;
        _lastFmAuthService = lastFmAuthService;
        _listenBrainzScrobblerService = listenBrainzScrobblerService;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLastFmNotConnected))]
    public partial bool IsLastFmConnected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLastFmInitialAuthEnabled))]
    public partial bool IsConnectingToLastFm { get; set; }

    [ObservableProperty]
    public partial string? LastFmUsername { get; set; }
    [ObservableProperty] public partial bool IsLastFmScrobblingEnabled { get; set; }
    [ObservableProperty] public partial bool IsLastFmNowPlayingEnabled { get; set; }

    public bool IsLastFmNotConnected => !IsLastFmConnected;
    public bool IsLastFmInitialAuthEnabled => !IsConnectingToLastFm;

    [ObservableProperty] public partial string ListenBrainzUserToken { get; set; } = string.Empty;
    [ObservableProperty] public partial bool ListenBrainzScrobblingEnabled { get; set; }
    [ObservableProperty] public partial bool ListenBrainzNowPlayingEnabled { get; set; }
    [ObservableProperty] public partial string ListenBrainzServerUrl { get; set; } = string.Empty;
    [ObservableProperty] public partial string? ListenBrainzConnectionStatus { get; set; }
    [ObservableProperty] public partial bool ListenBrainzIsConnected { get; set; }

    /// <summary>
    ///     Reads the saved account state without writing anything back.
    /// </summary>
    public async Task LoadAsync()
    {
        var lastFmCredsTask = _settingsService.GetLastFmCredentialsAsync();
        var lastFmAuthTokenTask = _settingsService.GetLastFmAuthTokenAsync();
        var scrobblingTask = _settingsService.GetLastFmScrobblingEnabledAsync();
        var nowPlayingTask = _settingsService.GetLastFmNowPlayingEnabledAsync();
        var lbTokenTask = _settingsService.GetListenBrainzUserTokenAsync();
        var lbScrobblingTask = _settingsService.GetListenBrainzScrobblingEnabledAsync();
        var lbNowPlayingTask = _settingsService.GetListenBrainzNowPlayingEnabledAsync();
        var lbServerUrlTask = _settingsService.GetListenBrainzServerUrlAsync();

        await Task.WhenAll(lastFmCredsTask, lastFmAuthTokenTask, scrobblingTask, nowPlayingTask,
            lbTokenTask, lbScrobblingTask, lbNowPlayingTask, lbServerUrlTask);

        if (_isDisposed) return;

        ApplyWithoutSaving(() =>
        {
            var lastFmCredentials = lastFmCredsTask.Result;
            LastFmUsername = lastFmCredentials?.Username;
            IsLastFmConnected = lastFmCredentials is not null && !string.IsNullOrEmpty(lastFmCredentials.Value.SessionKey);

            IsLastFmScrobblingEnabled = scrobblingTask.Result;
            IsLastFmNowPlayingEnabled = nowPlayingTask.Result;

            var authToken = lastFmAuthTokenTask.Result;
            IsConnectingToLastFm = !string.IsNullOrEmpty(authToken);

            ListenBrainzUserToken = lbTokenTask.Result ?? string.Empty;
            ListenBrainzIsConnected = !string.IsNullOrEmpty(ListenBrainzUserToken);
            ListenBrainzScrobblingEnabled = lbScrobblingTask.Result;
            ListenBrainzNowPlayingEnabled = lbNowPlayingTask.Result;
            ListenBrainzServerUrl = lbServerUrlTask.Result ?? string.Empty;
            ListenBrainzConnectionStatus = null;
        });
    }

    private bool IsLastFmAuthCurrent(int version) => !_isDisposed && version == _lastFmAuthVersion;

    [RelayCommand]
    private async Task LastFmInitialAuthAsync()
    {
        if (_isDisposed) return;
        var version = ++_lastFmAuthVersion;
        IsConnectingToLastFm = true;
        try
        {
            var authData = await _lastFmAuthService.GetAuthenticationTokenAsync();
            if (!IsLastFmAuthCurrent(version)) return;
            if (authData is { Token: not null, AuthUrl: not null })
            {
                await _settingsService.SaveLastFmAuthTokenAsync(authData.Value.Token);
                if (await _uiService.OpenUriAsync(new Uri(authData.Value.AuthUrl))) return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start Last.fm authentication.");
        }

        if (!IsLastFmAuthCurrent(version)) return;
        IsConnectingToLastFm = false;
        await SaveSettingAsync(() => _settingsService.SaveLastFmAuthTokenAsync(null));
        await _uiService.ShowMessageDialogAsync(Strings.Settings_LastFm_AuthError_Title, Strings.Settings_LastFm_AuthError_Message);
    }

    [RelayCommand]
    private async Task LastFmFinalizeAuthAsync()
    {
        if (_isDisposed) return;
        var version = _lastFmAuthVersion;
        try
        {
            var authToken = await _settingsService.GetLastFmAuthTokenAsync();
            if (string.IsNullOrEmpty(authToken)) return;

            var sessionData = await _lastFmAuthService.GetSessionAsync(authToken);
            if (!IsLastFmAuthCurrent(version)) return;
            if (sessionData is { Username: not null, SessionKey: not null })
            {
                await _settingsService.SaveLastFmCredentialsAsync(sessionData.Value.Username, sessionData.Value.SessionKey);
                await Task.WhenAll(_settingsService.SetLastFmScrobblingEnabledAsync(true),
                    _settingsService.SetLastFmNowPlayingEnabledAsync(true));

                ApplyWithoutSaving(() =>
                {
                    LastFmUsername = sessionData.Value.Username;
                    IsLastFmConnected = true;
                    IsLastFmScrobblingEnabled = true;
                    IsLastFmNowPlayingEnabled = true;
                });
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to finish Last.fm authentication.");
        }
        finally
        {
            if (IsLastFmAuthCurrent(version))
            {
                IsConnectingToLastFm = false;
                await SaveSettingAsync(() => _settingsService.SaveLastFmAuthTokenAsync(null));
            }
        }

        if (IsLastFmAuthCurrent(version))
            await _uiService.ShowMessageDialogAsync(Strings.Settings_LastFm_FinalizeError_Title,
                Strings.Settings_LastFm_FinalizeError_Message);
    }

    [RelayCommand]
    private async Task CancelLastFmAuthAsync()
    {
        ++_lastFmAuthVersion;
        IsConnectingToLastFm = false;
        await SaveSettingAsync(() => _settingsService.SaveLastFmAuthTokenAsync(null));
    }

    [RelayCommand]
    private async Task LastFmDisconnectAsync()
    {
        var confirmed = await _uiService.ShowConfirmationDialogAsync(
            Strings.Settings_LastFm_Disconnect_Title,
            Strings.Settings_LastFm_Disconnect_Message,
            Strings.Settings_LastFm_Disconnect_Button,
            null);

        if (!confirmed) return;

        ++_lastFmAuthVersion;
        try
        {
            await _settingsService.ClearLastFmCredentialsAsync();
            ApplyWithoutSaving(() =>
            {
                IsLastFmConnected = false;
                LastFmUsername = null;
                IsConnectingToLastFm = false;
                IsLastFmScrobblingEnabled = false;
                IsLastFmNowPlayingEnabled = false;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to disconnect Last.fm.");
        }
    }

    partial void OnIsLastFmScrobblingEnabledChanged(bool value) =>
        _ = SaveSettingAsync(() => _settingsService.SetLastFmScrobblingEnabledAsync(value));

    partial void OnIsLastFmNowPlayingEnabledChanged(bool value) =>
        _ = SaveSettingAsync(() => _settingsService.SetLastFmNowPlayingEnabledAsync(value));

    async partial void OnListenBrainzScrobblingEnabledChanged(bool value)
    {
        if (_isInitializing) return;
        try
        {
            await _settingsService.SetListenBrainzScrobblingEnabledAsync(value);
            if (value) await StampListenBrainzEnabledSinceIfNeededAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist ListenBrainz scrobbling toggle");
        }
    }

    async partial void OnListenBrainzNowPlayingEnabledChanged(bool value)
    {
        if (_isInitializing) return;
        try
        {
            await _settingsService.SetListenBrainzNowPlayingEnabledAsync(value);
            if (value) await StampListenBrainzEnabledSinceIfNeededAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist ListenBrainz now-playing toggle");
        }
    }

    async partial void OnListenBrainzServerUrlChanged(string value)
    {
        if (_isInitializing) return;
        ++_listenBrainzConnectionVersion;
        try
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            await _settingsService.SetListenBrainzServerUrlAsync(normalized);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist ListenBrainz server URL");
        }
    }

    /// <summary>
    ///     Stamps <see cref="ISettingsService.SetListenBrainzEnabledSinceUtcAsync"/> with the current UTC time
    ///     the first time any ListenBrainz submission is enabled, so historic listens are not backfilled.
    /// </summary>
    private async Task StampListenBrainzEnabledSinceIfNeededAsync()
    {
        try
        {
            if (await _settingsService.GetListenBrainzEnabledSinceUtcAsync() is null)
            {
                await _settingsService.SetListenBrainzEnabledSinceUtcAsync(DateTime.UtcNow);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stamp ListenBrainz EnabledSinceUtc");
        }
    }

    [RelayCommand]
    private async Task TestListenBrainzConnectionAsync()
    {
        if (_isDisposed) return;
        var version = ++_listenBrainzConnectionVersion;
        try
        {
            var token = ListenBrainzUserToken;
            var server = string.IsNullOrWhiteSpace(ListenBrainzServerUrl) ? null : ListenBrainzServerUrl;
            var result = await _listenBrainzScrobblerService.ValidateTokenAsync(token, server);
            if (_isDisposed || version != _listenBrainzConnectionVersion) return;
            if (result.IsValid)
            {
                await _settingsService.SaveListenBrainzUserTokenAsync(token);
                await StampListenBrainzEnabledSinceIfNeededAsync();
                ListenBrainzIsConnected = true;
                ListenBrainzConnectionStatus = string.Format(
                    Strings.SettingsPage_ListenBrainz_Connected,
                    result.Username);
            }
            else
            {
                ListenBrainzIsConnected = false;
                ListenBrainzConnectionStatus = string.Format(
                    Strings.SettingsPage_ListenBrainz_ConnectionFailed,
                    result.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ListenBrainz token validation failed");
            if (_isDisposed || version != _listenBrainzConnectionVersion) return;
            ListenBrainzIsConnected = false;
            ListenBrainzConnectionStatus = string.Format(
                Strings.SettingsPage_ListenBrainz_ConnectionFailed,
                "Could not reach ListenBrainz.");
        }
    }

    [RelayCommand]
    private async Task DisconnectListenBrainzAsync()
    {
        var confirmed = await _uiService.ShowConfirmationDialogAsync(
            Strings.SettingsPage_ListenBrainz_Disconnect_Title,
            Strings.SettingsPage_ListenBrainz_Disconnect_Content,
            Strings.SettingsPage_ListenBrainz_Disconnect_Button,
            null);

        if (!confirmed || _isDisposed) return;
        ++_listenBrainzConnectionVersion;

        try
        {
            await _settingsService.ClearListenBrainzUserTokenAsync();
            await _settingsService.SetListenBrainzScrobblingEnabledAsync(false);
            await _settingsService.SetListenBrainzNowPlayingEnabledAsync(false);
            await _settingsService.SetListenBrainzEnabledSinceUtcAsync(null);

            ApplyWithoutSaving(() =>
            {
                ListenBrainzScrobblingEnabled = false;
                ListenBrainzNowPlayingEnabled = false;
            });

            ListenBrainzUserToken = string.Empty;
            ListenBrainzIsConnected = false;
            ListenBrainzConnectionStatus = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to disconnect ListenBrainz");
        }
    }

    partial void OnListenBrainzUserTokenChanged(string value) => ++_listenBrainzConnectionVersion;
}

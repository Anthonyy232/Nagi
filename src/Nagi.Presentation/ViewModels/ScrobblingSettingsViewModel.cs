using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Settings;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;
using CoreStrings = Nagi.Core.Resources.Strings;
using Nagi.Presentation.Helpers;

namespace Nagi.Presentation.ViewModels;

/// <summary>
///     Settings section for Last.fm and ListenBrainz accounts and their submission toggles.
/// </summary>
public partial class ScrobblingSettingsViewModel : SettingsSectionViewModel
{
    private readonly IUIService _uiService;
    private readonly ILastFmAuthService _lastFmAuthService;
    private readonly IListenBrainzScrobblerService _listenBrainzScrobblerService;
    private int _lastFmAuthVersion;
    private int _listenBrainzConnectionVersion;

    public ScrobblingSettingsViewModel(
        ISettingsService settingsService,
        IUIService uiService,
        ILastFmAuthService lastFmAuthService,
        IListenBrainzScrobblerService listenBrainzScrobblerService,
        IDispatcherService dispatcherService,
        ILogger<ScrobblingSettingsViewModel> logger)
        : base(settingsService, dispatcherService, logger)
    {
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

    public bool IsLastFmScrobblingEnabled
    {
        get => Get(CoreSettings.LastFmScrobbling);
        set => Set(CoreSettings.LastFmScrobbling, value);
    }

    public bool IsLastFmNowPlayingEnabled
    {
        get => Get(CoreSettings.LastFmNowPlaying);
        set => Set(CoreSettings.LastFmNowPlaying, value);
    }

    public bool IsLastFmNotConnected => !IsLastFmConnected;
    public bool IsLastFmInitialAuthEnabled => !IsConnectingToLastFm;

    [ObservableProperty] public partial string ListenBrainzUserToken { get; set; } = string.Empty;

    public bool ListenBrainzScrobblingEnabled
    {
        get => Get(CoreSettings.ListenBrainzScrobbling);
        set
        {
            if (Set(CoreSettings.ListenBrainzScrobbling, value) && value) StampListenBrainzEnabledSinceIfNeeded();
        }
    }

    public bool ListenBrainzNowPlayingEnabled
    {
        get => Get(CoreSettings.ListenBrainzNowPlaying);
        set
        {
            if (Set(CoreSettings.ListenBrainzNowPlaying, value) && value) StampListenBrainzEnabledSinceIfNeeded();
        }
    }

    /// <summary>Empty for the default server.</summary>
    public string ListenBrainzServerUrl
    {
        get => Get(CoreSettings.ListenBrainzServerUrl) ?? string.Empty;
        set
        {
            if (value == ListenBrainzServerUrl) return;
            ++_listenBrainzConnectionVersion;
            Set(CoreSettings.ListenBrainzServerUrl, string.IsNullOrWhiteSpace(value) ? null : value.Trim());
        }
    }

    [ObservableProperty] public partial string? ListenBrainzConnectionStatus { get; set; }
    [ObservableProperty] public partial bool ListenBrainzIsConnected { get; set; }

    /// <summary>
    ///     Reads the saved account state without writing anything back.
    /// </summary>
    public async Task LoadAsync()
    {
        var lastFmCredentialsTask = _settingsService.GetLastFmCredentialsAsync();
        var listenBrainzTokenTask = _settingsService.GetListenBrainzUserTokenAsync();

        await Task.WhenAll(lastFmCredentialsTask, listenBrainzTokenTask);

        if (_isDisposed) return;

        ApplyWithoutSaving(() =>
        {
            var lastFmCredentials = lastFmCredentialsTask.Result;
            LastFmUsername = lastFmCredentials?.Username;
            IsLastFmConnected = lastFmCredentials is not null && !string.IsNullOrEmpty(lastFmCredentials.Value.SessionKey);
            IsConnectingToLastFm = !string.IsNullOrEmpty(_settingsService.Get(CoreSettings.LastFmAuthToken));

            ListenBrainzUserToken = listenBrainzTokenTask.Result ?? string.Empty;
            ListenBrainzIsConnected = !string.IsNullOrEmpty(ListenBrainzUserToken);
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
                _settingsService.Set(CoreSettings.LastFmAuthToken, authData.Value.Token);
                if (await _uiService.OpenUriAsync(new Uri(authData.Value.AuthUrl))) return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start Last.fm authentication.");
        }

        if (!IsLastFmAuthCurrent(version)) return;
        IsConnectingToLastFm = false;
        Set(CoreSettings.LastFmAuthToken, null);
        await _uiService.ShowMessageDialogAsync(Strings.Settings_LastFm_AuthError_Title, Strings.Settings_LastFm_AuthError_Message);
    }

    [RelayCommand]
    private async Task LastFmFinalizeAuthAsync()
    {
        if (_isDisposed) return;
        var version = _lastFmAuthVersion;
        var failureMessage = Strings.Settings_LastFm_FinalizeError_Message;
        try
        {
            var authToken = _settingsService.Get(CoreSettings.LastFmAuthToken);
            if (string.IsNullOrEmpty(authToken)) return;

            var sessionData = await _lastFmAuthService.GetSessionAsync(authToken);
            if (!IsLastFmAuthCurrent(version)) return;
            if (sessionData is { Username: not null, SessionKey: not null })
            {
                if (await _settingsService.SaveLastFmCredentialsAsync(sessionData.Value.Username, sessionData.Value.SessionKey))
                {
                    _settingsService.Set(CoreSettings.LastFmScrobbling, true);
                    _settingsService.Set(CoreSettings.LastFmNowPlaying, true);

                    LastFmUsername = sessionData.Value.Username;
                    IsLastFmConnected = true;
                    return;
                }

                failureMessage = Strings.MusicServer_Error_SignInNotSaved;
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
                Set(CoreSettings.LastFmAuthToken, null);
            }
        }

        if (IsLastFmAuthCurrent(version))
            await _uiService.ShowMessageDialogAsync(Strings.Settings_LastFm_FinalizeError_Title, failureMessage);
    }

    [RelayCommand]
    private void CancelLastFmAuth()
    {
        ++_lastFmAuthVersion;
        IsConnectingToLastFm = false;
        Set(CoreSettings.LastFmAuthToken, null);
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
            IsLastFmConnected = false;
            LastFmUsername = null;
            IsConnectingToLastFm = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to disconnect Last.fm.");
        }
    }

    // The first time any ListenBrainz submission is enabled, so historic listens are not backfilled.
    private void StampListenBrainzEnabledSinceIfNeeded()
    {
        if (_settingsService.Get(CoreSettings.ListenBrainzEnabledSinceUtc) is null)
            Set(CoreSettings.ListenBrainzEnabledSinceUtc, DateTime.UtcNow);
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
            if (result.IsValid && !await _settingsService.SaveListenBrainzUserTokenAsync(token))
            {
                ListenBrainzIsConnected = false;
                ListenBrainzConnectionStatus = ResourceFormatter.Format(
                    Strings.SettingsPage_ListenBrainz_ConnectionFailed,
                    Strings.MusicServer_Error_SignInNotSaved);
            }
            else if (result.IsValid)
            {
                StampListenBrainzEnabledSinceIfNeeded();
                ListenBrainzIsConnected = true;
                ListenBrainzConnectionStatus = ResourceFormatter.Format(
                    Strings.SettingsPage_ListenBrainz_Connected,
                    result.Username);
            }
            else
            {
                ListenBrainzIsConnected = false;
                ListenBrainzConnectionStatus = ResourceFormatter.Format(
                    Strings.SettingsPage_ListenBrainz_ConnectionFailed,
                    result.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ListenBrainz token validation failed");
            if (_isDisposed || version != _listenBrainzConnectionVersion) return;
            ListenBrainzIsConnected = false;
            ListenBrainzConnectionStatus = ResourceFormatter.Format(
                Strings.SettingsPage_ListenBrainz_ConnectionFailed,
                CoreStrings.ListenBrainz_Unreachable);
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
            _settingsService.Set(CoreSettings.ListenBrainzScrobbling, false);
            _settingsService.Set(CoreSettings.ListenBrainzNowPlaying, false);
            _settingsService.Set(CoreSettings.ListenBrainzEnabledSinceUtc, null);

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

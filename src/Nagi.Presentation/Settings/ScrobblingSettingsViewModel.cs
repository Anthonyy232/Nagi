using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Platform;
using Nagi.Core.Scrobbling;
using Nagi.Core.Settings;
using Nagi.Presentation.Common;
using Nagi.Presentation.Resources;
using CoreStrings = Nagi.Core.Resources.Strings;

namespace Nagi.Presentation.Settings;

/// <summary>
///     Settings section for Last.fm and ListenBrainz accounts and their submission toggles.
/// </summary>
public partial class ScrobblingSettingsViewModel : SettingsSectionViewModel
{
    private readonly IUIService _uiService;
    private readonly IListenBrainzScrobblerService _listenBrainzScrobblerService;
    private readonly IScrobblingAccounts _accounts;
    private int _listenBrainzConnectionVersion;

    public ScrobblingSettingsViewModel(
        ISettingsService settingsService,
        IUIService uiService,
        IListenBrainzScrobblerService listenBrainzScrobblerService,
        IScrobblingAccounts accounts,
        IDispatcherService dispatcherService,
        ILogger<ScrobblingSettingsViewModel> logger)
        : base(settingsService, dispatcherService, logger)
    {
        _uiService = uiService;
        _listenBrainzScrobblerService = listenBrainzScrobblerService;
        _accounts = accounts;
        _accounts.CredentialsChanged += OnCredentialsChanged;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLastFmNotConnected))]
    public partial bool IsLastFmConnected { get; set; }

    /// <summary>Whether a Last.fm sign-in waits for the user to approve it in the browser.</summary>
    public bool IsConnectingToLastFm => !string.IsNullOrEmpty(Get(CoreSettings.LastFmAuthToken));

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
        set => Set(CoreSettings.ListenBrainzScrobbling, value);
    }

    public bool ListenBrainzNowPlayingEnabled
    {
        get => Get(CoreSettings.ListenBrainzNowPlaying);
        set => Set(CoreSettings.ListenBrainzNowPlaying, value);
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
        await Task.WhenAll(ReadAccountAsync(ScrobblingAccount.LastFm), ReadAccountAsync(ScrobblingAccount.ListenBrainz));
        if (!_isDisposed) ListenBrainzConnectionStatus = null;
    }

    protected override void OnDisposing() => _accounts.CredentialsChanged -= OnCredentialsChanged;

    protected override void OnSettingChanged(Setting setting)
    {
        if (setting == CoreSettings.LastFmAuthToken) OnPropertyChanged(nameof(IsLastFmInitialAuthEnabled));
    }

    // A sign-in made here, or a reset elsewhere, changes what the page shows.
    private void OnCredentialsChanged(ScrobblingAccount account)
    {
        if (_dispatcherService.HasThreadAccess) _ = ReadAccountAsync(account);
        else _dispatcherService.TryEnqueue(() => _ = ReadAccountAsync(account));
    }

    private async Task ReadAccountAsync(ScrobblingAccount account)
    {
        try
        {
            if (account == ScrobblingAccount.LastFm)
            {
                var credentials = await _accounts.GetLastFmCredentialsAsync();
                if (_isDisposed) return;
                IsLastFmConnected = !string.IsNullOrEmpty(credentials?.SessionKey);
            }
            else
            {
                var token = await _accounts.GetListenBrainzUserTokenAsync();
                if (_isDisposed) return;
                ListenBrainzUserToken = token ?? string.Empty;
                ListenBrainzIsConnected = !string.IsNullOrEmpty(token);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read the {Account} account.", account);
        }
    }

    [RelayCommand]
    private async Task LastFmInitialAuthAsync()
    {
        if (_isDisposed) return;
        try
        {
            var start = await _accounts.BeginLastFmSignInAsync();
            if (start.WasCancelled || _isDisposed) return;
            if (start.ApprovalPage is { } page && await _uiService.OpenUriAsync(page)) return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start Last.fm authentication.");
        }

        if (_isDisposed) return;
        _accounts.CancelLastFmSignIn();
        await _uiService.ShowMessageDialogAsync(Strings.Settings_LastFm_AuthError_Title, Strings.Settings_LastFm_AuthError_Message);
    }

    [RelayCommand]
    private async Task LastFmFinalizeAuthAsync()
    {
        if (_isDisposed) return;
        LastFmSignInResult result;
        try
        {
            result = await _accounts.FinishLastFmSignInAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to finish Last.fm authentication.");
            result = LastFmSignInResult.Failed;
        }

        if (_isDisposed) return;
        if (result is LastFmSignInResult.Failed or LastFmSignInResult.NotSaved)
            await _uiService.ShowMessageDialogAsync(Strings.Settings_LastFm_FinalizeError_Title,
                result == LastFmSignInResult.NotSaved
                    ? Strings.Generic_SignInNotSaved
                    : Strings.Settings_LastFm_FinalizeError_Message);
    }

    [RelayCommand]
    private void CancelLastFmAuth() => _accounts.CancelLastFmSignIn();

    [RelayCommand]
    private async Task LastFmDisconnectAsync()
    {
        var confirmed = await _uiService.ShowConfirmationDialogAsync(
            Strings.Settings_LastFm_Disconnect_Title,
            Strings.Settings_LastFm_Disconnect_Message,
            Strings.Generic_Disconnect);

        if (!confirmed) return;

        try
        {
            await _accounts.DisconnectLastFmAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to disconnect Last.fm.");
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
            var result = await _listenBrainzScrobblerService.ValidateTokenAsync(token, ListenBrainzServerUrl);
            if (_isDisposed || version != _listenBrainzConnectionVersion) return;
            if (result.IsValid && !await _accounts.ConnectListenBrainzAsync(token))
            {
                ListenBrainzConnectionStatus = ResourceFormatter.Format(
                    Strings.SettingsPage_ListenBrainz_ConnectionFailed,
                    Strings.Generic_SignInNotSaved);
            }
            else if (result.IsValid)
            {
                ListenBrainzConnectionStatus = ResourceFormatter.Format(
                    Strings.SettingsPage_ListenBrainz_Connected,
                    result.Username);
            }
            else
            {
                ListenBrainzConnectionStatus = ResourceFormatter.Format(
                    Strings.SettingsPage_ListenBrainz_ConnectionFailed,
                    result.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ListenBrainz token validation failed");
            if (_isDisposed || version != _listenBrainzConnectionVersion) return;
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
            Strings.Generic_Disconnect);

        if (!confirmed || _isDisposed) return;
        ++_listenBrainzConnectionVersion;

        try
        {
            await _accounts.DisconnectListenBrainzAsync();
            ListenBrainzConnectionStatus = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to disconnect ListenBrainz");
        }
    }

    partial void OnListenBrainzUserTokenChanged(string value) => ++_listenBrainzConnectionVersion;
}

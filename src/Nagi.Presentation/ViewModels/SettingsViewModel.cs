using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Platform;
using Nagi.Core.Settings;
using Nagi.Presentation.Helpers;
using Nagi.Presentation.Models;
using Nagi.Presentation.Services;

namespace Nagi.Presentation.ViewModels;

/// <summary>
///     ViewModel for the Settings page. Owns the player and startup behavior settings directly and
///     exposes the self-contained sections (<see cref="Appearance" />, <see cref="Audio" />,
///     <see cref="Library" />, <see cref="Scrobbling" />, <see cref="Romanization" />) as child view
///     models, loading and disposing them together, plus <see cref="DataManagement" />, which has nothing to load or dispose.
/// </summary>
public partial class SettingsViewModel : SettingsSectionViewModel
{
    private readonly IAppInfoService _appInfoService;
    private readonly IAutoLaunchService _autoLaunchService;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    private bool _isLoaded;

    public SettingsViewModel(
        ISettingsService settingsService,
        IAppInfoService appInfoService,
        IAutoLaunchService autoLaunchService,
        IDispatcherService dispatcherService,
        AppearanceSettingsViewModel appearance,
        AudioSettingsViewModel audio,
        LibrarySettingsViewModel library,
        ScrobblingSettingsViewModel scrobbling,
        RomanizationPacksSettingsViewModel romanizationPacks,
        DataManagementSettingsViewModel dataManagement,
        ILogger<SettingsViewModel> logger,
        TimeProvider? timeProvider = null)
        : base(settingsService, dispatcherService, logger)
    {
        _appInfoService = appInfoService;
        _autoLaunchService = autoLaunchService;
        Appearance = appearance;
        Audio = audio;
        Library = library;
        Scrobbling = scrobbling;
        Romanization = romanizationPacks;
        DataManagement = dataManagement;
        PlayerButtons = new ReorderableToggleList<PlayerButtonSetting>(
            buttons => Set(AppSettings.PlayerButtons, buttons.ToList(), nameof(PlayerButtons)), dispatcherService,
            timeProvider);
    }

    public AppearanceSettingsViewModel Appearance { get; }
    public AudioSettingsViewModel Audio { get; }
    public LibrarySettingsViewModel Library { get; }
    public ScrobblingSettingsViewModel Scrobbling { get; }
    public RomanizationPacksSettingsViewModel Romanization { get; }
    public DataManagementSettingsViewModel DataManagement { get; }

    public bool IsPlayerAnimationEnabled
    {
        get => Get(AppSettings.PlayerAnimation);
        set => Set(AppSettings.PlayerAnimation, value);
    }

    public bool IsRestorePlaybackStateEnabled
    {
        get => Get(CoreSettings.RestorePlaybackState);
        set => Set(CoreSettings.RestorePlaybackState, value);
    }

    public bool IsStartMinimizedEnabled
    {
        get => Get(AppSettings.StartMinimized);
        set => Set(AppSettings.StartMinimized, value);
    }

    public bool IsHideToTrayEnabled
    {
        get => Get(AppSettings.HideToTray);
        set => Set(AppSettings.HideToTray, value);
    }

    public bool IsMinimizeToMiniPlayerEnabled
    {
        get => Get(AppSettings.MinimizeToMiniPlayer);
        set => Set(AppSettings.MinimizeToMiniPlayer, value);
    }

    public bool IsShowCoverArtInTrayFlyoutEnabled
    {
        get => Get(AppSettings.ShowCoverArtInTrayFlyout);
        set => Set(AppSettings.ShowCoverArtInTrayFlyout, value);
    }

    public bool IsDiscordRichPresenceEnabled
    {
        get => Get(CoreSettings.DiscordRichPresence);
        set => Set(CoreSettings.DiscordRichPresence, value);
    }

    // Launching at sign-in is the operating system's state, read when the page loads.
    [ObservableProperty] public partial bool IsAutoLaunchEnabled { get; set; }
    [ObservableProperty] public partial bool IsUpdatingAutoLaunch { get; set; }

    public ReorderableToggleList<PlayerButtonSetting> PlayerButtons { get; }

    public string ApplicationVersion => _appInfoService.GetAppVersion();

    protected override void OnDisposing()
    {
        PlayerButtons.Dispose();
        Appearance.Dispose();
        Audio.Dispose();
        Library.Dispose();
        Scrobbling.Dispose();
        Romanization.Dispose();
    }

    /// <summary>
    ///     Loads the settings page: the saved settings, then the current audio devices, with the romanization packs
    ///     loading in the background.
    /// </summary>
    public async Task OpenAsync()
    {
        await LoadSettingsAsync();
        await Audio.RefreshAudioOutputDevicesCommand.ExecuteAsync(null);
        _ = Romanization.LoadRomanizationPacksAsync();
    }

    /// <summary>
    ///     Loads what the settings store doesn't hold directly: the editable lists, the equalizer, the accounts and
    ///     launching at sign-in. The view models are singletons, so this loads once per app session.
    /// </summary>
    public async Task LoadSettingsAsync()
    {
        if (_isDisposed || _isLoaded) return;

        await _loadLock.WaitAsync();
        try
        {
            if (_isDisposed || _isLoaded) return;

            var autoLaunchTask = _autoLaunchService.IsEnabledAsync();
            var scrobblingLoadTask = Scrobbling.LoadAsync();
            var audioLoadTask = Audio.LoadAsync();
            Appearance.Load();
            Library.Load();
            PlayerButtons.Load(_settingsService.Get(AppSettings.PlayerButtons));

            await Task.WhenAll(autoLaunchTask, scrobblingLoadTask, audioLoadTask);
            if (_isDisposed) return;

            ApplyWithoutSaving(() => IsAutoLaunchEnabled = autoLaunchTask.Result);
            _isLoaded = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load settings.");
        }
        finally
        {
            _loadLock.Release();
        }
    }

    [RelayCommand]
    private void ResetPlayerButtons() => PlayerButtons.Reset(AppSettings.PlayerButtons.Default);

    internal Task PendingSettingsSaves => Task.WhenAll(
        Appearance.PendingSettingsSaves,
        Audio.PendingSettingsSaves,
        Library.PendingSettingsSaves,
        PlayerButtons.PendingSave);

    async partial void OnIsAutoLaunchEnabledChanged(bool value)
    {
        if (_isInitializing || _isDisposed || IsUpdatingAutoLaunch) return;
        IsUpdatingAutoLaunch = true;
        try
        {
            await _autoLaunchService.SetEnabledAsync(value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to change startup setting.");
        }

        var enabled = await _autoLaunchService.IsEnabledAsync();
        if (!_isDisposed) IsAutoLaunchEnabled = enabled;
        IsUpdatingAutoLaunch = false;
    }
}

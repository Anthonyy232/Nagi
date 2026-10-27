using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Helpers;
using Nagi.Presentation.Models;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.Services;

namespace Nagi.Presentation.ViewModels;

/// <summary>
///     ViewModel for the Settings page. Owns the player and startup behavior settings directly and
///     exposes the self-contained sections (<see cref="Appearance" />, <see cref="Audio" />,
///     <see cref="Library" />, <see cref="Scrobbling" />, <see cref="Romanization" />,
///     <see cref="DataManagement" />) as child view models, loading and disposing them together.
/// </summary>
public partial class SettingsViewModel : SettingsSectionViewModel
{
    private readonly IAppInfoService _appInfoService;
    private readonly IAutoLaunchService _autoLaunchService;
    private readonly IUISettingsService _settingsService;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    private const int SettingsSaveDebounceMs = 300;
    private readonly TimeProvider _timeProvider;

    private bool _isLoaded;
    private readonly Debouncer _playerButtonSaveDebouncer;

    public SettingsViewModel(
        IUISettingsService settingsService,
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
        : base(dispatcherService, logger)
    {
        _settingsService = settingsService;
        _appInfoService = appInfoService;
        _autoLaunchService = autoLaunchService;
        Appearance = appearance;
        Audio = audio;
        Library = library;
        Scrobbling = scrobbling;
        Romanization = romanizationPacks;
        DataManagement = dataManagement;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _playerButtonSaveDebouncer = new Debouncer(TimeSpan.FromMilliseconds(SettingsSaveDebounceMs), _timeProvider);

        PlayerButtons.CollectionChanged += OnPlayerButtonsCollectionChanged;

        _isInitializing = true;

        IsPlayerAnimationEnabled = SettingsDefaults.PlayerAnimationEnabled;
        IsRestorePlaybackStateEnabled = SettingsDefaults.RestorePlaybackStateEnabled;
        IsAutoLaunchEnabled = SettingsDefaults.AutoLaunchEnabled;
        IsStartMinimizedEnabled = SettingsDefaults.StartMinimizedEnabled;
        IsHideToTrayEnabled = SettingsDefaults.HideToTrayEnabled;
        IsMinimizeToMiniPlayerEnabled = SettingsDefaults.MinimizeToMiniPlayerEnabled;
        IsShowCoverArtInTrayFlyoutEnabled = SettingsDefaults.ShowCoverArtInTrayFlyoutEnabled;
        IsDiscordRichPresenceEnabled = SettingsDefaults.DiscordRichPresenceEnabled;

        _isInitializing = false;
    }

    public AppearanceSettingsViewModel Appearance { get; }
    public AudioSettingsViewModel Audio { get; }
    public LibrarySettingsViewModel Library { get; }
    public ScrobblingSettingsViewModel Scrobbling { get; }
    public RomanizationPacksSettingsViewModel Romanization { get; }
    public DataManagementSettingsViewModel DataManagement { get; }

    [ObservableProperty] public partial bool IsPlayerAnimationEnabled { get; set; }
    [ObservableProperty] public partial bool IsRestorePlaybackStateEnabled { get; set; }
    [ObservableProperty] public partial bool IsAutoLaunchEnabled { get; set; }
    [ObservableProperty] public partial bool IsUpdatingAutoLaunch { get; set; }
    [ObservableProperty] public partial bool IsStartMinimizedEnabled { get; set; }
    [ObservableProperty] public partial bool IsHideToTrayEnabled { get; set; }
    [ObservableProperty] public partial bool IsMinimizeToMiniPlayerEnabled { get; set; }
    [ObservableProperty] public partial bool IsShowCoverArtInTrayFlyoutEnabled { get; set; }
    [ObservableProperty] public partial bool IsDiscordRichPresenceEnabled { get; set; }

    public ObservableRangeCollection<PlayerButtonSetting> PlayerButtons { get; } = new();

    public string ApplicationVersion => _appInfoService.GetAppVersion();

    protected override void OnDisposing()
    {
        PlayerButtons.CollectionChanged -= OnPlayerButtonsCollectionChanged;

        foreach (var item in PlayerButtons) item.PropertyChanged -= OnPlayerButtonPropertyChanged;

        _playerButtonSaveDebouncer.Dispose();
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

    public async Task LoadSettingsAsync()
    {
        if (_isDisposed) return;

        // If already loaded, don't reload everything.
        // This ViewModel is a singleton, so we only need to load once per app session.
        if (_isLoaded) return;

        await _loadLock.WaitAsync();
        try
        {
            if (_isDisposed || _isLoaded) return;

            _isInitializing = true;

            foreach (var item in PlayerButtons) item.PropertyChanged -= OnPlayerButtonPropertyChanged;
            PlayerButtons.Clear();

            var playerButtonsTask = _settingsService.GetPlayerButtonSettingsAsync();
            var playerAnimationTask = _settingsService.GetPlayerAnimationEnabledAsync();
            var restorePlaybackTask = _settingsService.GetRestorePlaybackStateEnabledAsync();
            var autoLaunchTask = _autoLaunchService.IsEnabledAsync();
            var startMinimizedTask = _settingsService.GetStartMinimizedEnabledAsync();
            var hideToTrayTask = _settingsService.GetHideToTrayEnabledAsync();
            var miniPlayerTask = _settingsService.GetMinimizeToMiniPlayerEnabledAsync();
            var trayFlyoutTask = _settingsService.GetShowCoverArtInTrayFlyoutAsync();
            var discordRpcTask = _settingsService.GetDiscordRichPresenceEnabledAsync();
            var appearanceLoadTask = Appearance.LoadAsync();
            var audioLoadTask = Audio.LoadAsync();
            var libraryLoadTask = Library.LoadAsync();
            var scrobblingLoadTask = Scrobbling.LoadAsync();

            await Task.WhenAll(
                playerButtonsTask, playerAnimationTask, restorePlaybackTask, autoLaunchTask,
                startMinimizedTask, hideToTrayTask, miniPlayerTask, trayFlyoutTask, discordRpcTask,
                appearanceLoadTask, audioLoadTask, libraryLoadTask, scrobblingLoadTask);

            if (_isDisposed) return;

            foreach (var button in playerButtonsTask.Result)
            {
                button.PropertyChanged += OnPlayerButtonPropertyChanged;
            }
            PlayerButtons.ReplaceRange(playerButtonsTask.Result);

            IsPlayerAnimationEnabled = playerAnimationTask.Result;
            IsRestorePlaybackStateEnabled = restorePlaybackTask.Result;
            IsAutoLaunchEnabled = autoLaunchTask.Result;
            IsStartMinimizedEnabled = startMinimizedTask.Result;
            IsHideToTrayEnabled = hideToTrayTask.Result;
            IsMinimizeToMiniPlayerEnabled = miniPlayerTask.Result;
            IsShowCoverArtInTrayFlyoutEnabled = trayFlyoutTask.Result;
            IsDiscordRichPresenceEnabled = discordRpcTask.Result;
            _isLoaded = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load settings.");
        }
        finally
        {
            _isInitializing = false;
            _loadLock.Release();
        }
    }

    [RelayCommand]
    private async Task ResetPlayerButtonsAsync()
    {
        _playerButtonSaveDebouncer.Cancel();
        var defaultButtons = _settingsService.GetDefaultPlayerButtonSettings();
        ApplyWithoutSaving(() =>
        {
            foreach (var item in PlayerButtons) item.PropertyChanged -= OnPlayerButtonPropertyChanged;
            foreach (var button in defaultButtons) button.PropertyChanged += OnPlayerButtonPropertyChanged;
            PlayerButtons.ReplaceRange(defaultButtons);
        });

        await _settingsService.SetPlayerButtonSettingsAsync(defaultButtons);
    }

    private void OnPlayerButtonsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        if (e.NewItems != null)
            foreach (PlayerButtonSetting item in e.NewItems)
                item.PropertyChanged += OnPlayerButtonPropertyChanged;

        if (e.OldItems != null)
            foreach (PlayerButtonSetting item in e.OldItems)
                item.PropertyChanged -= OnPlayerButtonPropertyChanged;

        QueuePlayerButtonSave();
    }

    private void OnPlayerButtonPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isInitializing || e.PropertyName != nameof(PlayerButtonSetting.IsEnabled)) return;
        QueuePlayerButtonSave();
    }

    /// <summary>
    ///     Queues a debounced save for player button settings to prevent rapid saves during drag operations.
    /// </summary>
    private void QueuePlayerButtonSave()
    {
        var snapshot = PlayerButtons.ToList();
        QueueSettingsSave(_playerButtonSaveDebouncer, _ => _settingsService.SetPlayerButtonSettingsAsync(snapshot), nameof(PlayerButtons));
    }

    internal Task PendingSettingsSaves => Task.WhenAll(
        Appearance.PendingSettingsSaves,
        Audio.PendingSettingsSaves,
        Library.PendingSettingsSaves,
        _playerButtonSaveDebouncer.PendingInvocation);

    partial void OnIsPlayerAnimationEnabledChanged(bool value) =>
        _ = SaveSettingAsync(() => _settingsService.SetPlayerAnimationEnabledAsync(value));

    partial void OnIsRestorePlaybackStateEnabledChanged(bool value) =>
        _ = SaveSettingAsync(() => _settingsService.SetRestorePlaybackStateEnabledAsync(value));

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

        try
        {
            var enabled = await _autoLaunchService.IsEnabledAsync();
            if (!_isDisposed) IsAutoLaunchEnabled = enabled;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read startup setting.");
        }
        finally
        {
            IsUpdatingAutoLaunch = false;
        }
    }

    partial void OnIsStartMinimizedEnabledChanged(bool value) =>
        _ = SaveSettingAsync(() => _settingsService.SetStartMinimizedEnabledAsync(value));

    partial void OnIsHideToTrayEnabledChanged(bool value) =>
        _ = SaveSettingAsync(() => _settingsService.SetHideToTrayEnabledAsync(value));

    partial void OnIsMinimizeToMiniPlayerEnabledChanged(bool value) =>
        _ = SaveSettingAsync(() => _settingsService.SetMinimizeToMiniPlayerEnabledAsync(value));

    partial void OnIsShowCoverArtInTrayFlyoutEnabledChanged(bool value) =>
        _ = SaveSettingAsync(() => _settingsService.SetShowCoverArtInTrayFlyoutAsync(value));

    partial void OnIsDiscordRichPresenceEnabledChanged(bool value) =>
        _ = SaveSettingAsync(() => _settingsService.SetDiscordRichPresenceEnabledAsync(value));


}

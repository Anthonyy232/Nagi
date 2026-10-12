using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Nagi.WinUI.Resources;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Models;
using Nagi.WinUI.Navigation;
using Nagi.WinUI.Services;
using Nagi.WinUI.Services.Abstractions;
using Microsoft.Windows.AppLifecycle;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     ViewModel for the Settings page. Owns the general settings directly and exposes the
///     self-contained sections (<see cref="Audio" />, <see cref="Scrobbling" />,
///     <see cref="Romanization" />, <see cref="DataManagement" />) as child view models.
/// </summary>
public partial class SettingsViewModel : SettingsSectionViewModel
{
    private const int VisualSettingDebounceMs = 200;

    private readonly IAppInfoService _appInfoService;
    private readonly IUISettingsService _settingsService;
    private readonly IThemeService _themeService;
    private readonly IUIService _uiService;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    private const int SettingsSaveDebounceMs = 300;
    private readonly TimeProvider _timeProvider;

    private bool _isLoaded;
    private readonly Debouncer _playerButtonSaveDebouncer;
    private readonly Debouncer _navigationItemSaveDebouncer;
    private readonly Debouncer _playerTintSaveDebouncer;
    private readonly Debouncer _accentColorSaveDebouncer;

    public SettingsViewModel(
        IUISettingsService settingsService,
        IUIService uiService,
        IThemeService themeService,
        IAppInfoService appInfoService,
        IDispatcherService dispatcherService,
        AudioSettingsViewModel audio,
        LibrarySettingsViewModel library,
        ScrobblingSettingsViewModel scrobbling,
        RomanizationPacksSettingsViewModel romanizationPacks,
        DataManagementSettingsViewModel dataManagement,
        ILogger<SettingsViewModel> logger,
        TimeProvider? timeProvider = null)
        : base(dispatcherService, logger)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _uiService = uiService ?? throw new ArgumentNullException(nameof(uiService));
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        _appInfoService = appInfoService ?? throw new ArgumentNullException(nameof(appInfoService));
        Audio = audio ?? throw new ArgumentNullException(nameof(audio));
        Library = library ?? throw new ArgumentNullException(nameof(library));
        Scrobbling = scrobbling ?? throw new ArgumentNullException(nameof(scrobbling));
        Romanization = romanizationPacks ?? throw new ArgumentNullException(nameof(romanizationPacks));
        DataManagement = dataManagement ?? throw new ArgumentNullException(nameof(dataManagement));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _playerButtonSaveDebouncer = new Debouncer(TimeSpan.FromMilliseconds(SettingsSaveDebounceMs), _timeProvider);
        _navigationItemSaveDebouncer = new Debouncer(TimeSpan.FromMilliseconds(SettingsSaveDebounceMs), _timeProvider);
        _playerTintSaveDebouncer = new Debouncer(TimeSpan.FromMilliseconds(VisualSettingDebounceMs), _timeProvider);
        _accentColorSaveDebouncer = new Debouncer(TimeSpan.FromMilliseconds(VisualSettingDebounceMs), _timeProvider);

        NavigationItems.CollectionChanged += OnNavigationItemsCollectionChanged;
        PlayerButtons.CollectionChanged += OnPlayerButtonsCollectionChanged;

        _isInitializing = true;

        SelectedTheme = SettingsDefaults.Theme;
        SelectedBackdropMaterial = SettingsDefaults.DefaultBackdropMaterial;
        IsDynamicThemingEnabled = SettingsDefaults.DynamicThemingEnabled;
        IsPlayerAnimationEnabled = SettingsDefaults.PlayerAnimationEnabled;
        IsRestorePlaybackStateEnabled = SettingsDefaults.RestorePlaybackStateEnabled;
        IsAutoLaunchEnabled = SettingsDefaults.AutoLaunchEnabled;
        IsStartMinimizedEnabled = SettingsDefaults.StartMinimizedEnabled;
        IsHideToTrayEnabled = SettingsDefaults.HideToTrayEnabled;
        IsMinimizeToMiniPlayerEnabled = SettingsDefaults.MinimizeToMiniPlayerEnabled;
        IsShowCoverArtInTrayFlyoutEnabled = SettingsDefaults.ShowCoverArtInTrayFlyoutEnabled;
        IsDiscordRichPresenceEnabled = SettingsDefaults.DiscordRichPresenceEnabled;
        IsRememberWindowSizeEnabled = SettingsDefaults.RememberWindowSizeEnabled;
        IsRememberWindowPositionEnabled = SettingsDefaults.RememberWindowPositionEnabled;
        IsRememberPaneStateEnabled = SettingsDefaults.RememberPaneStateEnabled;

        _isInitializing = false;
    }

    public AudioSettingsViewModel Audio { get; }
    public LibrarySettingsViewModel Library { get; }
    public ScrobblingSettingsViewModel Scrobbling { get; }
    public RomanizationPacksSettingsViewModel Romanization { get; }
    public DataManagementSettingsViewModel DataManagement { get; }


    [ObservableProperty] public partial ElementTheme SelectedTheme { get; set; }
    [ObservableProperty] public partial BackdropMaterial SelectedBackdropMaterial { get; set; }
    [ObservableProperty] public partial bool IsDynamicThemingEnabled { get; set; }
    [ObservableProperty] public partial bool IsPlayerAnimationEnabled { get; set; }
    [ObservableProperty] public partial bool IsRestorePlaybackStateEnabled { get; set; }
    [ObservableProperty] public partial bool IsAutoLaunchEnabled { get; set; }
    [ObservableProperty] public partial bool IsUpdatingAutoLaunch { get; set; }
    [ObservableProperty] public partial bool IsStartMinimizedEnabled { get; set; }
    [ObservableProperty] public partial bool IsHideToTrayEnabled { get; set; }
    [ObservableProperty] public partial bool IsMinimizeToMiniPlayerEnabled { get; set; }
    [ObservableProperty] public partial bool IsShowCoverArtInTrayFlyoutEnabled { get; set; }
    [ObservableProperty] public partial bool IsDiscordRichPresenceEnabled { get; set; }
    [ObservableProperty] public partial bool IsRememberWindowSizeEnabled { get; set; }
    [ObservableProperty] public partial bool IsRememberWindowPositionEnabled { get; set; }
    [ObservableProperty] public partial bool IsRememberPaneStateEnabled { get; set; }
    [ObservableProperty] public partial Windows.UI.Color AccentColor { get; set; }

    async partial void OnSelectedLanguageChanged(LanguageModel? value)
    {
        if (_isInitializing || _isDisposed || value == null) return;

        try
        {
            var currentLanguage = await _settingsService.GetLanguageAsync();
            if (string.Equals(value.Code, currentLanguage, StringComparison.OrdinalIgnoreCase)) return;

            await _settingsService.SetLanguageAsync(value.Code);

            var title = Strings.SettingsPage_RestartRequired_Title;
            var message = Strings.SettingsPage_RestartRequired_Message;

            await _uiService.ShowMessageDialogAsync(title, message);
            AppInstance.Restart("");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set language to {LanguageCode}", value.Code);
        }
    }

    public ObservableRangeCollection<PlayerButtonSetting> PlayerButtons { get; } = new();
    public ObservableRangeCollection<NavigationItemSetting> NavigationItems { get; } = new();


    public IReadOnlyList<ElementTheme> AvailableThemes { get; } = Enum.GetValues<ElementTheme>();
    public IReadOnlyList<BackdropMaterial> AvailableBackdropMaterials { get; } = Enum.GetValues<BackdropMaterial>();
    public IReadOnlyList<PlayerBackgroundMaterial> AvailablePlayerBackgroundMaterials { get; } = Enum.GetValues<PlayerBackgroundMaterial>();

    [ObservableProperty] public partial PlayerBackgroundMaterial SelectedPlayerBackgroundMaterial { get; set; }
    [ObservableProperty] public partial double PlayerTintIntensity { get; set; }

    partial void OnSelectedPlayerBackgroundMaterialChanged(PlayerBackgroundMaterial value) =>
        _ = SaveSettingAsync(() => _settingsService.SetPlayerBackgroundMaterialAsync(value));

    partial void OnPlayerTintIntensityChanged(double value)
    {
        if (_isInitializing) return;
        QueueSettingsSave(_playerTintSaveDebouncer, _ => _settingsService.SetPlayerTintIntensityAsync(value), nameof(PlayerTintIntensity));
    }

    public ObservableRangeCollection<LanguageModel> AvailableLanguages { get; } = new();

    [ObservableProperty] public partial LanguageModel? SelectedLanguage { get; set; }

    public string ApplicationVersion => _appInfoService.GetAppVersion();

    protected override void OnDisposing()
    {
        NavigationItems.CollectionChanged -= OnNavigationItemsCollectionChanged;
        PlayerButtons.CollectionChanged -= OnPlayerButtonsCollectionChanged;

        foreach (var item in NavigationItems) item.PropertyChanged -= OnNavigationItemPropertyChanged;

        foreach (var item in PlayerButtons) item.PropertyChanged -= OnPlayerButtonPropertyChanged;

        _playerButtonSaveDebouncer.Dispose();
        _navigationItemSaveDebouncer.Dispose();
        _playerTintSaveDebouncer.Dispose();
        _accentColorSaveDebouncer.Dispose();
        Audio.Dispose();
        Library.Dispose();
        Scrobbling.Dispose();
        Romanization.Dispose();
    }

    [RelayCommand]
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

            foreach (var item in NavigationItems) item.PropertyChanged -= OnNavigationItemPropertyChanged;
            NavigationItems.Clear();

            foreach (var item in PlayerButtons) item.PropertyChanged -= OnPlayerButtonPropertyChanged;
            PlayerButtons.Clear();

            var navItemsTask = _settingsService.GetNavigationItemsAsync();
            var playerButtonsTask = _settingsService.GetPlayerButtonSettingsAsync();
            var themeTask = _settingsService.GetThemeAsync();
            var backdropTask = _settingsService.GetBackdropMaterialAsync();
            var dynamicThemingTask = _settingsService.GetDynamicThemingAsync();
            var playerAnimationTask = _settingsService.GetPlayerAnimationEnabledAsync();
            var restorePlaybackTask = _settingsService.GetRestorePlaybackStateEnabledAsync();
            var autoLaunchTask = _settingsService.GetAutoLaunchEnabledAsync();
            var startMinimizedTask = _settingsService.GetStartMinimizedEnabledAsync();
            var hideToTrayTask = _settingsService.GetHideToTrayEnabledAsync();
            var miniPlayerTask = _settingsService.GetMinimizeToMiniPlayerEnabledAsync();
            var trayFlyoutTask = _settingsService.GetShowCoverArtInTrayFlyoutAsync();
            var discordRpcTask = _settingsService.GetDiscordRichPresenceEnabledAsync();
            var rememberWindowTask = _settingsService.GetRememberWindowSizeEnabledAsync();
            var rememberPositionTask = _settingsService.GetRememberWindowPositionEnabledAsync();
            var rememberPaneTask = _settingsService.GetRememberPaneStateEnabledAsync();
            var audioLoadTask = Audio.LoadAsync();
            var libraryLoadTask = Library.LoadAsync();
            var scrobblingLoadTask = Scrobbling.LoadAsync();

            var accentColorTask = _settingsService.GetAccentColorAsync();
            var languageTask = _settingsService.GetLanguageAsync();

            var playerMaterialTask = _settingsService.GetPlayerBackgroundMaterialAsync();
            var playerTintTask = _settingsService.GetPlayerTintIntensityAsync();

            await Task.WhenAll(
                navItemsTask, playerButtonsTask, themeTask, backdropTask, dynamicThemingTask,
                playerAnimationTask, restorePlaybackTask, autoLaunchTask, startMinimizedTask,
                hideToTrayTask, miniPlayerTask, trayFlyoutTask, discordRpcTask, rememberWindowTask,
                rememberPositionTask, rememberPaneTask, audioLoadTask, libraryLoadTask,
                scrobblingLoadTask, accentColorTask, languageTask,
                playerMaterialTask, playerTintTask);

            if (_isDisposed) return;

            foreach (var item in navItemsTask.Result)
            {
                item.PropertyChanged += OnNavigationItemPropertyChanged;
            }
            NavigationItems.ReplaceRange(navItemsTask.Result);

            foreach (var button in playerButtonsTask.Result)
            {
                button.PropertyChanged += OnPlayerButtonPropertyChanged;
            }
            PlayerButtons.ReplaceRange(playerButtonsTask.Result);

            SelectedTheme = themeTask.Result;
            SelectedBackdropMaterial = backdropTask.Result;
            IsDynamicThemingEnabled = dynamicThemingTask.Result;
            IsPlayerAnimationEnabled = playerAnimationTask.Result;
            IsRestorePlaybackStateEnabled = restorePlaybackTask.Result;
            IsAutoLaunchEnabled = autoLaunchTask.Result;
            IsStartMinimizedEnabled = startMinimizedTask.Result;
            IsHideToTrayEnabled = hideToTrayTask.Result;
            IsMinimizeToMiniPlayerEnabled = miniPlayerTask.Result;
            IsShowCoverArtInTrayFlyoutEnabled = trayFlyoutTask.Result;
            IsDiscordRichPresenceEnabled = discordRpcTask.Result;
            IsRememberWindowSizeEnabled = rememberWindowTask.Result;
            IsRememberWindowPositionEnabled = rememberPositionTask.Result;
            IsRememberPaneStateEnabled = rememberPaneTask.Result;

            SelectedPlayerBackgroundMaterial = playerMaterialTask.Result;
            PlayerTintIntensity = playerTintTask.Result;

            AccentColor = accentColorTask.Result ?? SettingsDefaults.BrandAccentColor;

            // Initialize with a minimal list immediately so the binding is valid on first render.
            // The full language list is populated asynchronously to avoid blocking startup.
            AvailableLanguages.Clear();
            AvailableLanguages.Add(new LanguageModel(string.Empty, Nagi.WinUI.Resources.Strings.Language_Auto));
            AvailableLanguages.Add(new LanguageModel("en-US", "English"));

            var currentLangCode = languageTask.Result;
            SelectedLanguage = ResolveSelectedLanguage(currentLangCode);

            _ = PopulateAvailableLanguagesAsync(currentLangCode);

            _isLoaded = true;
        }
        finally
        {
            _isInitializing = false;
            _loadLock.Release();
        }
    }

    /// <summary>
    /// Resolves the best LanguageModel match for <paramref name="langCode"/> from AvailableLanguages.
    /// Must be called on the UI thread (reads the ObservableCollection).
    /// </summary>
    private LanguageModel ResolveSelectedLanguage(string langCode) =>
        AvailableLanguages.FirstOrDefault(l => string.Equals(l.Code, langCode, StringComparison.OrdinalIgnoreCase))
        ?? AvailableLanguages.FirstOrDefault(l => !string.IsNullOrEmpty(l.Code) && !string.IsNullOrEmpty(langCode) &&
                                                  (l.Code.StartsWith(langCode + "-", StringComparison.OrdinalIgnoreCase) || langCode.StartsWith(l.Code + "-", StringComparison.OrdinalIgnoreCase)))
        ?? AvailableLanguages.First(l => l.Code == string.Empty);

    /// <summary>
    /// Populates AvailableLanguages asynchronously by scanning for satellite resource assemblies.
    /// Fires and forgets from LoadSettingsAsync — does not block startup.
    /// Updates SelectedLanguage once the full list is available.
    /// </summary>
    private async Task PopulateAvailableLanguagesAsync(string currentLangCode)
    {
        var initialSelection = SelectedLanguage;
        try
        {
            var languageCodes = await _appInfoService.GetAvailableLanguagesAsync().ConfigureAwait(false);
            var candidates = languageCodes.Select(code =>
                {
                    try
                    {
                        var culture = CultureInfo.GetCultureInfo(code);
                        return new LanguageModel(culture.Name, culture.NativeName);
                    }
                    catch (CultureNotFoundException) { return null; }
                })
                .OfType<LanguageModel>()
                .ToList();

            if (candidates.Count == 0) return;
            _dispatcherService.TryEnqueue(() =>
            {
                if (_isDisposed) return;
                var selection = ReferenceEquals(SelectedLanguage, initialSelection) ? currentLangCode : SelectedLanguage?.Code;
                ApplyWithoutSaving(() =>
                {
                    AvailableLanguages.ReplaceRange(AvailableLanguages.Concat(candidates)
                        .DistinctBy(language => language.Code, StringComparer.OrdinalIgnoreCase));
                    SelectedLanguage = ResolveSelectedLanguage(selection ?? string.Empty);
                });
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to populate available languages.");
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
        Audio.PendingSettingsSaves,
        Library.PendingSettingsSaves,
        _playerButtonSaveDebouncer.PendingInvocation,
        _navigationItemSaveDebouncer.PendingInvocation,
        _playerTintSaveDebouncer.PendingInvocation,
        _accentColorSaveDebouncer.PendingInvocation);

    private void OnNavigationItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (NavigationItemSetting item in e.NewItems)
                item.PropertyChanged += OnNavigationItemPropertyChanged;

        if (e.OldItems != null)
            foreach (NavigationItemSetting item in e.OldItems)
                item.PropertyChanged -= OnNavigationItemPropertyChanged;

        if (!_isInitializing)
            QueueNavigationItemSave();
    }

    private void OnNavigationItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isInitializing || e.PropertyName != nameof(NavigationItemSetting.IsEnabled)) return;
        QueueNavigationItemSave();
    }

    /// <summary>
    ///     Queues a debounced save for navigation items to prevent rapid saves.
    /// </summary>
    private void QueueNavigationItemSave()
    {
        var snapshot = NavigationItems.ToList();
        QueueSettingsSave(_navigationItemSaveDebouncer, _ => _settingsService.SetNavigationItemsAsync(snapshot), nameof(NavigationItems));
    }

    async partial void OnSelectedThemeChanged(ElementTheme value)
    {
        if (_isInitializing) return;
        try
        {
            await _settingsService.SetThemeAsync(value);
            _themeService.ApplyTheme(value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error changing app theme to {Theme}", value);
        }
    }

    partial void OnSelectedBackdropMaterialChanged(BackdropMaterial value) =>
        _ = SaveSettingAsync(() => _settingsService.SetBackdropMaterialAsync(value));

    async partial void OnIsDynamicThemingEnabledChanged(bool value)
    {
        if (_isInitializing) return;
        try
        {
            await _settingsService.SetDynamicThemingAsync(value);
            if (value)
            {
                await _themeService.ReapplyCurrentDynamicThemeAsync();
            }
            else
            {
                var accentColor = await _settingsService.GetAccentColorAsync();
                await _themeService.ApplyAccentColorAsync(accentColor);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating dynamic theming to {Value}", value);
        }
    }

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
            await _settingsService.SetAutoLaunchEnabledAsync(value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to change startup setting.");
        }

        try
        {
            var enabled = await _settingsService.GetAutoLaunchEnabledAsync();
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


    partial void OnIsRememberWindowSizeEnabledChanged(bool value) =>
        _ = SaveSettingAsync(() => _settingsService.SetRememberWindowSizeEnabledAsync(value));

    partial void OnIsRememberWindowPositionEnabledChanged(bool value) =>
        _ = SaveSettingAsync(() => _settingsService.SetRememberWindowPositionEnabledAsync(value));

    partial void OnIsRememberPaneStateEnabledChanged(bool value) =>
        _ = SaveSettingAsync(() => _settingsService.SetRememberPaneStateEnabledAsync(value));

    partial void OnAccentColorChanged(Windows.UI.Color value)
    {
        if (_isInitializing) return;
        QueueSettingsSave(_accentColorSaveDebouncer, async token =>
        {
            await _settingsService.SetAccentColorAsync(value);
            token.ThrowIfCancellationRequested();
            if (!IsDynamicThemingEnabled)
                await _themeService.ApplyAccentColorAsync(value);
        }, nameof(AccentColor));
    }

    [RelayCommand]
    private async Task ResetAccentColorAsync()
    {
        _accentColorSaveDebouncer.Cancel();
        ApplyWithoutSaving(() => AccentColor = SettingsDefaults.BrandAccentColor);

        await _settingsService.SetAccentColorAsync(null);
        if (!IsDynamicThemingEnabled)
        {
            await _themeService.ApplyAccentColorAsync(null);
        }
    }

    [RelayCommand]
    private async Task ResetPlayerDesignSettingsAsync()
    {
        _playerTintSaveDebouncer.Cancel();
        ApplyWithoutSaving(() =>
        {
            SelectedPlayerBackgroundMaterial = SettingsDefaults.DefaultPlayerBackgroundMaterial;
            PlayerTintIntensity = SettingsDefaults.DefaultPlayerTintIntensity;
        });

        await _settingsService.SetPlayerBackgroundMaterialAsync(SettingsDefaults.DefaultPlayerBackgroundMaterial);
        await _settingsService.SetPlayerTintIntensityAsync(SettingsDefaults.DefaultPlayerTintIntensity);
    }
}

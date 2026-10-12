using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Models;
using Nagi.WinUI.Navigation;
using Nagi.WinUI.Resources;
using Nagi.WinUI.Services;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     Settings section for how the app looks and is laid out: theme, accent color, backdrop, player
///     styling, display language, remembered window layout and the navigation menu.
/// </summary>
public partial class AppearanceSettingsViewModel : SettingsSectionViewModel
{
    private const int SettingsSaveDebounceMs = 300;
    private const int VisualSettingDebounceMs = 200;

    private readonly IUISettingsService _settingsService;
    private readonly IThemeService _themeService;
    private readonly IUIService _uiService;
    private readonly IAppInfoService _appInfoService;
    private readonly Debouncer _navigationItemSaveDebouncer;
    private readonly Debouncer _playerTintSaveDebouncer;
    private readonly Debouncer _accentColorSaveDebouncer;

    public AppearanceSettingsViewModel(
        IUISettingsService settingsService,
        IThemeService themeService,
        IUIService uiService,
        IAppInfoService appInfoService,
        IDispatcherService dispatcherService,
        ILogger<AppearanceSettingsViewModel> logger,
        TimeProvider? timeProvider = null)
        : base(dispatcherService, logger)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        _uiService = uiService ?? throw new ArgumentNullException(nameof(uiService));
        _appInfoService = appInfoService ?? throw new ArgumentNullException(nameof(appInfoService));
        timeProvider ??= TimeProvider.System;
        _navigationItemSaveDebouncer = new Debouncer(TimeSpan.FromMilliseconds(SettingsSaveDebounceMs), timeProvider);
        _playerTintSaveDebouncer = new Debouncer(TimeSpan.FromMilliseconds(VisualSettingDebounceMs), timeProvider);
        _accentColorSaveDebouncer = new Debouncer(TimeSpan.FromMilliseconds(VisualSettingDebounceMs), timeProvider);

        NavigationItems.CollectionChanged += OnNavigationItemsCollectionChanged;

        ApplyWithoutSaving(() =>
        {
            SelectedTheme = SettingsDefaults.Theme;
            SelectedBackdropMaterial = SettingsDefaults.DefaultBackdropMaterial;
            IsDynamicThemingEnabled = SettingsDefaults.DynamicThemingEnabled;
            IsRememberWindowSizeEnabled = SettingsDefaults.RememberWindowSizeEnabled;
            IsRememberWindowPositionEnabled = SettingsDefaults.RememberWindowPositionEnabled;
            IsRememberPaneStateEnabled = SettingsDefaults.RememberPaneStateEnabled;
        });
    }

    [ObservableProperty] public partial ElementTheme SelectedTheme { get; set; }
    [ObservableProperty] public partial BackdropMaterial SelectedBackdropMaterial { get; set; }
    [ObservableProperty] public partial bool IsDynamicThemingEnabled { get; set; }
    [ObservableProperty] public partial Windows.UI.Color AccentColor { get; set; }
    [ObservableProperty] public partial PlayerBackgroundMaterial SelectedPlayerBackgroundMaterial { get; set; }
    [ObservableProperty] public partial double PlayerTintIntensity { get; set; }
    [ObservableProperty] public partial LanguageModel? SelectedLanguage { get; set; }
    [ObservableProperty] public partial bool IsRememberWindowSizeEnabled { get; set; }
    [ObservableProperty] public partial bool IsRememberWindowPositionEnabled { get; set; }
    [ObservableProperty] public partial bool IsRememberPaneStateEnabled { get; set; }

    public IReadOnlyList<ElementTheme> AvailableThemes { get; } = Enum.GetValues<ElementTheme>();
    public IReadOnlyList<BackdropMaterial> AvailableBackdropMaterials { get; } = Enum.GetValues<BackdropMaterial>();
    public IReadOnlyList<PlayerBackgroundMaterial> AvailablePlayerBackgroundMaterials { get; } = Enum.GetValues<PlayerBackgroundMaterial>();
    public ObservableRangeCollection<LanguageModel> AvailableLanguages { get; } = new();
    public ObservableRangeCollection<NavigationItemSetting> NavigationItems { get; } = new();

    internal Task PendingSettingsSaves => Task.WhenAll(
        _navigationItemSaveDebouncer.PendingInvocation,
        _playerTintSaveDebouncer.PendingInvocation,
        _accentColorSaveDebouncer.PendingInvocation);

    /// <summary>
    ///     Loads the saved values without writing anything back, then fills in the full language list
    ///     in the background.
    /// </summary>
    public async Task LoadAsync()
    {
        ApplyWithoutSaving(() =>
        {
            foreach (var item in NavigationItems) item.PropertyChanged -= OnNavigationItemPropertyChanged;
            NavigationItems.Clear();
        });

        var navItemsTask = _settingsService.GetNavigationItemsAsync();
        var themeTask = _settingsService.GetThemeAsync();
        var backdropTask = _settingsService.GetBackdropMaterialAsync();
        var dynamicThemingTask = _settingsService.GetDynamicThemingAsync();
        var accentColorTask = _settingsService.GetAccentColorAsync();
        var playerMaterialTask = _settingsService.GetPlayerBackgroundMaterialAsync();
        var playerTintTask = _settingsService.GetPlayerTintIntensityAsync();
        var languageTask = _settingsService.GetLanguageAsync();
        var rememberWindowTask = _settingsService.GetRememberWindowSizeEnabledAsync();
        var rememberPositionTask = _settingsService.GetRememberWindowPositionEnabledAsync();
        var rememberPaneTask = _settingsService.GetRememberPaneStateEnabledAsync();

        await Task.WhenAll(navItemsTask, themeTask, backdropTask, dynamicThemingTask, accentColorTask,
            playerMaterialTask, playerTintTask, languageTask, rememberWindowTask, rememberPositionTask,
            rememberPaneTask);

        if (_isDisposed) return;

        var currentLangCode = languageTask.Result;
        ApplyWithoutSaving(() =>
        {
            foreach (var item in navItemsTask.Result)
            {
                item.PropertyChanged += OnNavigationItemPropertyChanged;
            }
            NavigationItems.ReplaceRange(navItemsTask.Result);

            SelectedTheme = themeTask.Result;
            SelectedBackdropMaterial = backdropTask.Result;
            IsDynamicThemingEnabled = dynamicThemingTask.Result;
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

            SelectedLanguage = ResolveSelectedLanguage(currentLangCode);
        });

        _ = PopulateAvailableLanguagesAsync(currentLangCode);
    }

    protected override void OnDisposing()
    {
        NavigationItems.CollectionChanged -= OnNavigationItemsCollectionChanged;

        foreach (var item in NavigationItems) item.PropertyChanged -= OnNavigationItemPropertyChanged;

        _navigationItemSaveDebouncer.Dispose();
        _playerTintSaveDebouncer.Dispose();
        _accentColorSaveDebouncer.Dispose();
    }

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

    partial void OnSelectedPlayerBackgroundMaterialChanged(PlayerBackgroundMaterial value) =>
        _ = SaveSettingAsync(() => _settingsService.SetPlayerBackgroundMaterialAsync(value));

    partial void OnPlayerTintIntensityChanged(double value)
    {
        if (_isInitializing) return;
        QueueSettingsSave(_playerTintSaveDebouncer, _ => _settingsService.SetPlayerTintIntensityAsync(value), nameof(PlayerTintIntensity));
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

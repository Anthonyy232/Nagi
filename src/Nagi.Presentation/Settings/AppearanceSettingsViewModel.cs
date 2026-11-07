using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Platform;
using Nagi.Core.Settings;
using Nagi.Presentation.Common;
using Nagi.Presentation.Lists;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.Theme;

namespace Nagi.Presentation.Settings;

/// <summary>
///     Settings section for how the app looks and is laid out: theme, accent color, backdrop, player
///     styling, display language, remembered window layout, the navigation menu and song list columns.
/// </summary>
public partial class AppearanceSettingsViewModel : SettingsSectionViewModel
{
    private const int VisualSettingDebounceMs = 200;

    private readonly IUIService _uiService;
    private readonly IAppInfoService _appInfoService;
    private readonly IApplicationLifecycle _applicationLifecycle;
    private readonly Debouncer _playerTintSaveDebouncer;
    private readonly Debouncer _accentColorSaveDebouncer;

    // Dragging a slider or color picker re-themes the app on every save, so these values wait to be saved.
    private ArgbColor? _pendingAccentColor;
    private double? _pendingPlayerTintIntensity;

    public AppearanceSettingsViewModel(
        ISettingsService settingsService,
        IUIService uiService,
        IAppInfoService appInfoService,
        IApplicationLifecycle applicationLifecycle,
        IDispatcherService dispatcherService,
        ILogger<AppearanceSettingsViewModel> logger,
        TimeProvider? timeProvider = null)
        : base(settingsService, dispatcherService, logger)
    {
        _uiService = uiService;
        _appInfoService = appInfoService;
        _applicationLifecycle = applicationLifecycle;
        timeProvider ??= TimeProvider.System;
        _playerTintSaveDebouncer = new Debouncer(TimeSpan.FromMilliseconds(VisualSettingDebounceMs), timeProvider);
        _accentColorSaveDebouncer = new Debouncer(TimeSpan.FromMilliseconds(VisualSettingDebounceMs), timeProvider);

        NavigationItems = new ReorderableToggleList<NavigationItemSetting>(
            items => Set(AppSettings.NavigationItems, items.ToList(), nameof(NavigationItems)), dispatcherService,
            timeProvider);
        SetSongListColumnToggles(_settingsService.Get(AppSettings.SongListColumns));
        foreach (var setting in SongListColumnSettings) setting.PropertyChanged += OnSongListColumnSettingChanged;
    }

    public SettingOption<AppTheme> SelectedTheme
    {
        get => ThemeOptions.First(option => option.Value == Get(AppSettings.Theme));
        set => Set(AppSettings.Theme, value.Value);
    }

    public SettingOption<BackdropMaterial> SelectedBackdropMaterial
    {
        get => BackdropMaterialOptions.First(option => option.Value == Get(AppSettings.BackdropMaterial));
        set => Set(AppSettings.BackdropMaterial, value.Value);
    }

    public bool IsDynamicThemingEnabled
    {
        get => Get(AppSettings.DynamicTheming);
        set => Set(AppSettings.DynamicTheming, value);
    }

    public ArgbColor AccentColor
    {
        get => _pendingAccentColor ?? Get(AppSettings.AccentColor) ?? ThemePalette.BrandAccentColor;
        set
        {
            if (value == AccentColor) return;
            _pendingAccentColor = value;
            OnPropertyChanged();
            QueueSettingsSave(_accentColorSaveDebouncer, _ =>
            {
                Set(AppSettings.AccentColor, value);
                if (_pendingAccentColor == value) _pendingAccentColor = null;
                return Task.CompletedTask;
            }, nameof(AccentColor));
        }
    }

    public SettingOption<PlayerBackgroundMaterial> SelectedPlayerBackgroundMaterial
    {
        get => PlayerBackgroundMaterialOptions.First(option => option.Value == Get(AppSettings.PlayerBackgroundMaterial));
        set => Set(AppSettings.PlayerBackgroundMaterial, value.Value);
    }

    public double PlayerTintIntensity
    {
        get => _pendingPlayerTintIntensity ?? Get(AppSettings.PlayerTintIntensity);
        set
        {
            if (value == PlayerTintIntensity) return;
            _pendingPlayerTintIntensity = value;
            OnPropertyChanged();
            QueueSettingsSave(_playerTintSaveDebouncer, _ =>
            {
                Set(AppSettings.PlayerTintIntensity, value);
                if (_pendingPlayerTintIntensity == value) _pendingPlayerTintIntensity = null;
                return Task.CompletedTask;
            }, nameof(PlayerTintIntensity));
        }
    }

    [ObservableProperty] public partial LanguageModel? SelectedLanguage { get; set; }

    public bool IsRememberWindowSizeEnabled
    {
        get => Get(AppSettings.RememberWindowSize);
        set => Set(AppSettings.RememberWindowSize, value);
    }

    public bool IsRememberWindowPositionEnabled
    {
        get => Get(AppSettings.RememberWindowPosition);
        set => Set(AppSettings.RememberWindowPosition, value);
    }

    public bool IsRememberPaneStateEnabled
    {
        get => Get(AppSettings.RememberPaneState);
        set => Set(AppSettings.RememberPaneState, value);
    }

    public bool IsSongListHeaderEnabled
    {
        get => Get(AppSettings.ShowSongListHeader);
        set => Set(AppSettings.ShowSongListHeader, value);
    }

    public IReadOnlyList<SettingOption<AppTheme>> ThemeOptions { get; } =
        SettingOption<AppTheme>.For(Enum.GetValues<AppTheme>(), SettingsText.GetName);

    public IReadOnlyList<SettingOption<BackdropMaterial>> BackdropMaterialOptions { get; } =
        SettingOption<BackdropMaterial>.For(Enum.GetValues<BackdropMaterial>(), SettingsText.GetName);

    public IReadOnlyList<SettingOption<PlayerBackgroundMaterial>> PlayerBackgroundMaterialOptions { get; } =
        SettingOption<PlayerBackgroundMaterial>.For(Enum.GetValues<PlayerBackgroundMaterial>(), SettingsText.GetName);
    public ObservableRangeCollection<LanguageModel> AvailableLanguages { get; } = new();
    public ReorderableToggleList<NavigationItemSetting> NavigationItems { get; }

    public IReadOnlyList<SongListColumnSetting> SongListColumnSettings { get; } =
        SongListColumnsHelper.All.Select(column => new SongListColumnSetting(column)).ToList();

    internal Task PendingSettingsSaves => Task.WhenAll(
        NavigationItems.PendingSave,
        _playerTintSaveDebouncer.PendingInvocation,
        _accentColorSaveDebouncer.PendingInvocation);

    /// <summary>
    ///     Loads the navigation menu and the language choice without writing anything back, then fills in the full
    ///     language list in the background.
    /// </summary>
    public void Load()
    {
        NavigationItems.Load(_settingsService.Get(AppSettings.NavigationItems));
        var currentLangCode = _settingsService.Get(AppSettings.Language);
        ApplyWithoutSaving(() =>
        {
            // Initialize with a minimal list immediately so the binding is valid on first render.
            // The full language list is populated asynchronously to avoid blocking startup.
            AvailableLanguages.Clear();
            AvailableLanguages.Add(new LanguageModel(string.Empty, Strings.Language_Auto));
            AvailableLanguages.Add(new LanguageModel("en-US", "English"));

            SelectedLanguage = ResolveSelectedLanguage(currentLangCode);
        });

        _ = PopulateAvailableLanguagesAsync(currentLangCode);
    }

    protected override void OnSettingChanged(Setting setting)
    {
        if (setting == AppSettings.SongListColumns)
            ApplyWithoutSaving(() => SetSongListColumnToggles(_settingsService.Get(AppSettings.SongListColumns)));
    }

    protected override void OnDisposing()
    {
        NavigationItems.Dispose();
        foreach (var setting in SongListColumnSettings) setting.PropertyChanged -= OnSongListColumnSettingChanged;

        _playerTintSaveDebouncer.Dispose();
        _accentColorSaveDebouncer.Dispose();
    }

    async partial void OnSelectedLanguageChanged(LanguageModel? value)
    {
        if (_isInitializing || _isDisposed || value == null) return;

        try
        {
            var currentLanguage = _settingsService.Get(AppSettings.Language);
            if (string.Equals(value.Code, currentLanguage, StringComparison.OrdinalIgnoreCase)) return;

            _settingsService.Set(AppSettings.Language, value.Code);

            var title = Strings.SettingsPage_RestartRequired_Title;
            var message = Strings.SettingsPage_RestartRequired_Message;

            await _uiService.ShowMessageDialogAsync(title, message);
            _applicationLifecycle.Restart();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set language to {LanguageCode}", value.Code);
        }
    }

    // An exact match, else the same language in another region, else the system language.
    private LanguageModel ResolveSelectedLanguage(string langCode) =>
        AvailableLanguages.FirstOrDefault(l => string.Equals(l.Code, langCode, StringComparison.OrdinalIgnoreCase))
        ?? AvailableLanguages.FirstOrDefault(l => !string.IsNullOrEmpty(l.Code) && !string.IsNullOrEmpty(langCode) &&
                                                  (l.Code.StartsWith(langCode + "-", StringComparison.OrdinalIgnoreCase) || langCode.StartsWith(l.Code + "-", StringComparison.OrdinalIgnoreCase)))
        ?? AvailableLanguages.First(l => l.Code == string.Empty);

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

    private void OnSongListColumnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isInitializing) return;
        var columns = SongListColumnSettings.Where(setting => setting.IsEnabled)
            .Aggregate(SongListColumns.None, (all, setting) => all | setting.Column);
        Set(AppSettings.SongListColumns, columns, nameof(SongListColumnSettings));
    }

    [RelayCommand]
    private void ResetAccentColor()
    {
        _accentColorSaveDebouncer.Cancel();
        _pendingAccentColor = null;
        Set(AppSettings.AccentColor, null, nameof(AccentColor));
        OnPropertyChanged(nameof(AccentColor));
    }

    [RelayCommand]
    private void ResetPlayerDesignSettings()
    {
        _playerTintSaveDebouncer.Cancel();
        _pendingPlayerTintIntensity = null;
        Set(AppSettings.PlayerBackgroundMaterial, AppSettings.PlayerBackgroundMaterial.Default, nameof(SelectedPlayerBackgroundMaterial));
        Set(AppSettings.PlayerTintIntensity, AppSettings.PlayerTintIntensity.Default, nameof(PlayerTintIntensity));
        OnPropertyChanged(nameof(PlayerTintIntensity));
    }

    [RelayCommand]
    private void ResetSongListColumns() =>
        Set(AppSettings.SongListColumns, AppSettings.SongListColumns.Default, nameof(SongListColumnSettings));

    private void SetSongListColumnToggles(SongListColumns columns)
    {
        foreach (var setting in SongListColumnSettings) setting.IsEnabled = columns.HasFlag(setting.Column);
    }
}

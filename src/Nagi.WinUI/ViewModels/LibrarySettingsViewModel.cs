using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Services;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     Settings section for how the library is read and enriched: title sorting, artist and genre
///     splitting, metadata rescans, and the online metadata and lyrics providers.
/// </summary>
public partial class LibrarySettingsViewModel : SettingsSectionViewModel
{
    private const int SettingsSaveDebounceMs = 300;

    private readonly IUISettingsService _settingsService;
    private readonly IUIService _uiService;
    private readonly ILibraryScanner _libraryScanner;
    private readonly PlayerViewModel _playerViewModel;
    private readonly Debouncer _lyricsProviderSaveDebouncer;
    private readonly Debouncer _metadataProviderSaveDebouncer;
    private readonly Debouncer _artistSplitSaveDebouncer;
    private readonly Debouncer _genreSplitSaveDebouncer;
    private CancellationTokenSource? _metadataRescanCts;

    public LibrarySettingsViewModel(
        IUISettingsService settingsService,
        IUIService uiService,
        ILibraryScanner libraryScanner,
        PlayerViewModel playerViewModel,
        IDispatcherService dispatcherService,
        ILogger<LibrarySettingsViewModel> logger,
        TimeProvider? timeProvider = null)
        : base(dispatcherService, logger)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _uiService = uiService ?? throw new ArgumentNullException(nameof(uiService));
        _libraryScanner = libraryScanner ?? throw new ArgumentNullException(nameof(libraryScanner));
        _playerViewModel = playerViewModel ?? throw new ArgumentNullException(nameof(playerViewModel));
        _playerViewModel.PropertyChanged += OnPlayerViewModelPropertyChanged;
        timeProvider ??= TimeProvider.System;
        _lyricsProviderSaveDebouncer = new Debouncer(TimeSpan.FromMilliseconds(SettingsSaveDebounceMs), timeProvider);
        _metadataProviderSaveDebouncer = new Debouncer(TimeSpan.FromMilliseconds(SettingsSaveDebounceMs), timeProvider);
        _artistSplitSaveDebouncer = new Debouncer(TimeSpan.FromMilliseconds(SettingsSaveDebounceMs), timeProvider);
        _genreSplitSaveDebouncer = new Debouncer(TimeSpan.FromMilliseconds(SettingsSaveDebounceMs), timeProvider);

        LyricsProviders.CollectionChanged += OnLyricsProvidersCollectionChanged;
        MetadataProviders.CollectionChanged += OnMetadataProvidersCollectionChanged;

        ApplyWithoutSaving(() =>
        {
            IsFetchOnlineMetadataEnabled = SettingsDefaults.FetchOnlineMetadataEnabled;
            IsIgnoreLeadingArticlesOnSortEnabled = SettingsDefaults.IgnoreLeadingArticlesOnSortEnabled;
            IsFetchOnlineLyricsEnabled = SettingsDefaults.FetchOnlineLyricsEnabled;
            IsLyricsRomanizationEnabled = SettingsDefaults.LyricsRomanizationEnabled;
        });
    }

    [ObservableProperty] public partial bool IsFetchOnlineMetadataEnabled { get; set; }
    [ObservableProperty] public partial bool IsIgnoreLeadingArticlesOnSortEnabled { get; set; }
    [ObservableProperty] public partial bool IsFetchOnlineLyricsEnabled { get; set; }
    [ObservableProperty] public partial bool IsLyricsRomanizationEnabled { get; set; }
    [ObservableProperty] public partial string ArtistSplitCharacters { get; set; } = string.Empty;
    [ObservableProperty] public partial string GenreSplitCharacters { get; set; } = string.Empty;

    public ObservableRangeCollection<ServiceProviderSettingViewModel> LyricsProviders { get; } = new();
    public ObservableRangeCollection<ServiceProviderSettingViewModel> MetadataProviders { get; } = new();

    internal Task PendingSettingsSaves => Task.WhenAll(
        _lyricsProviderSaveDebouncer.PendingInvocation,
        _metadataProviderSaveDebouncer.PendingInvocation,
        _artistSplitSaveDebouncer.PendingInvocation,
        _genreSplitSaveDebouncer.PendingInvocation);

    /// <summary>
    ///     Loads the saved values without writing anything back.
    /// </summary>
    public async Task LoadAsync()
    {
        ApplyWithoutSaving(() =>
        {
            foreach (var item in LyricsProviders) item.PropertyChanged -= OnLyricsProviderPropertyChanged;
            LyricsProviders.Clear();

            foreach (var item in MetadataProviders) item.PropertyChanged -= OnMetadataProviderPropertyChanged;
            MetadataProviders.Clear();
        });

        var onlineMetadataTask = _settingsService.GetFetchOnlineMetadataEnabledAsync();
        var ignoreArticlesTask = _settingsService.GetIgnoreLeadingArticlesOnSortEnabledAsync();
        var onlineLyricsTask = _settingsService.GetFetchOnlineLyricsEnabledAsync();
        var lyricsRomanizationTask = _settingsService.GetLyricsRomanizationEnabledAsync();
        var artistSplitTask = _settingsService.GetArtistSplitCharactersAsync();
        var genreSplitTask = _settingsService.GetGenreSplitCharactersAsync();
        var lyricsProvidersTask = _settingsService.GetServiceProvidersAsync(ServiceCategory.Lyrics);
        var metadataProvidersTask = _settingsService.GetServiceProvidersAsync(ServiceCategory.Metadata);

        await Task.WhenAll(onlineMetadataTask, ignoreArticlesTask, onlineLyricsTask, lyricsRomanizationTask,
            artistSplitTask, genreSplitTask, lyricsProvidersTask, metadataProvidersTask);

        if (_isDisposed) return;

        ApplyWithoutSaving(() =>
        {
            IsFetchOnlineMetadataEnabled = onlineMetadataTask.Result;
            IsIgnoreLeadingArticlesOnSortEnabled = ignoreArticlesTask.Result;
            IsFetchOnlineLyricsEnabled = onlineLyricsTask.Result;
            IsLyricsRomanizationEnabled = lyricsRomanizationTask.Result;

            ArtistSplitCharacters = artistSplitTask.Result;
            GenreSplitCharacters = genreSplitTask.Result;

            var lyricsMapped = lyricsProvidersTask.Result
                .Select(ServiceProviderSettingViewModel.FromSetting)
                .ToList();
            foreach (var item in lyricsMapped)
                item.PropertyChanged += OnLyricsProviderPropertyChanged;
            LyricsProviders.ReplaceRange(lyricsMapped);

            var metadataProviders = metadataProvidersTask.Result
                .Select(ServiceProviderSettingViewModel.FromSetting)
                .OrderByDescending(provider => provider.Id == ServiceProviderIds.MusicBrainz)
                .ToList();

            foreach (var item in metadataProviders)
                item.PropertyChanged += OnMetadataProviderPropertyChanged;
            MetadataProviders.ReplaceRange(metadataProviders);
        });
    }

    protected override void OnDisposing()
    {
        LyricsProviders.CollectionChanged -= OnLyricsProvidersCollectionChanged;
        MetadataProviders.CollectionChanged -= OnMetadataProvidersCollectionChanged;

        foreach (var item in LyricsProviders) item.PropertyChanged -= OnLyricsProviderPropertyChanged;

        foreach (var item in MetadataProviders) item.PropertyChanged -= OnMetadataProviderPropertyChanged;

        _metadataRescanCts?.Cancel();
        _lyricsProviderSaveDebouncer.Dispose();
        _playerViewModel.PropertyChanged -= OnPlayerViewModelPropertyChanged;
        _metadataProviderSaveDebouncer.Dispose();
        _artistSplitSaveDebouncer.Dispose();
        _genreSplitSaveDebouncer.Dispose();
    }

    partial void OnArtistSplitCharactersChanged(string value)
    {
        if (_isInitializing) return;
        QueueSettingsSave(_artistSplitSaveDebouncer, _ => _settingsService.SetArtistSplitCharactersAsync(value), nameof(ArtistSplitCharacters));
    }

    partial void OnGenreSplitCharactersChanged(string value)
    {
        if (_isInitializing) return;
        QueueSettingsSave(_genreSplitSaveDebouncer, _ => _settingsService.SetGenreSplitCharactersAsync(value), nameof(GenreSplitCharacters));
    }

    private bool CanRescanMetadata() => !_playerViewModel.IsGlobalOperationInProgress;

    private void OnPlayerViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.IsGlobalOperationInProgress))
        {
            RescanMetadataCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRescanMetadata))]
    private async Task RescanMetadataAsync()
    {
        if (_playerViewModel.IsGlobalOperationInProgress) return;

        var confirmed = await _uiService.ShowConfirmationDialogAsync(
            Nagi.WinUI.Resources.Strings.Settings_Dialog_Rescan_Title,
            Nagi.WinUI.Resources.Strings.Settings_Dialog_Rescan_Content,
            Nagi.WinUI.Resources.Strings.Settings_Dialog_Rescan_PrimaryButton,
            null);

        if (!confirmed || _isDisposed || _playerViewModel.IsGlobalOperationInProgress) return;

        using var rescanCts = new CancellationTokenSource();
        _metadataRescanCts = rescanCts;
        var token = rescanCts.Token;

        _playerViewModel.IsGlobalOperationInProgress = true;
        _playerViewModel.GlobalOperationStatusMessage = Nagi.WinUI.Resources.Strings.Settings_Status_Rescan_Preparing;
        _playerViewModel.IsGlobalOperationIndeterminate = true;
        _playerViewModel.SetGlobalOperationCancellation(rescanCts.Cancel);

        try
        {
            await SaveMetadataSplitSettingsAsync();
            token.ThrowIfCancellationRequested();
            await _libraryScanner.ForceRescanMetadataAsync(CreateGlobalOperationProgress(_playerViewModel, token), token);
            token.ThrowIfCancellationRequested();

            await _uiService.ShowMessageDialogAsync(Nagi.WinUI.Resources.Strings.Settings_Dialog_RescanComplete_Title, Nagi.WinUI.Resources.Strings.Settings_Dialog_RescanComplete_Content);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Metadata rescan cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Metadata rescan failed.");
            await _uiService.ShowMessageDialogAsync(Nagi.WinUI.Resources.Strings.Settings_Dialog_RescanFailed_Title, Nagi.WinUI.Resources.Strings.Settings_Dialog_RescanFailed_Content);
        }
        finally
        {
            rescanCts.Cancel();
            _metadataRescanCts = null;
            _playerViewModel.SetGlobalOperationCancellation(null);
            _playerViewModel.IsGlobalOperationInProgress = false;
            _playerViewModel.IsGlobalOperationIndeterminate = false;
        }
    }

    private async Task SaveMetadataSplitSettingsAsync()
    {
        _artistSplitSaveDebouncer.Cancel();
        _genreSplitSaveDebouncer.Cancel();

        await Task.WhenAll(
            _settingsService.SetArtistSplitCharactersAsync(ArtistSplitCharacters),
            _settingsService.SetGenreSplitCharactersAsync(GenreSplitCharacters));
    }

    private void OnLyricsProvidersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (ServiceProviderSettingViewModel item in e.NewItems)
                item.PropertyChanged += OnLyricsProviderPropertyChanged;

        if (e.OldItems != null)
            foreach (ServiceProviderSettingViewModel item in e.OldItems)
                item.PropertyChanged -= OnLyricsProviderPropertyChanged;

        if (!_isInitializing)
            QueueLyricsProviderSave();
    }

    private void OnLyricsProviderPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isInitializing || e.PropertyName != nameof(ServiceProviderSettingViewModel.IsEnabled)) return;
        QueueLyricsProviderSave();
    }

    private void QueueLyricsProviderSave()
    {
        var snapshot = LyricsProviders.Select((vm, i) => vm.ToSetting(i)).ToList();
        QueueSettingsSave(_lyricsProviderSaveDebouncer, _ => _settingsService.SetServiceProvidersAsync(ServiceCategory.Lyrics, snapshot), nameof(LyricsProviders));
    }

    private void OnMetadataProvidersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (ServiceProviderSettingViewModel item in e.NewItems)
                item.PropertyChanged += OnMetadataProviderPropertyChanged;

        if (e.OldItems != null)
            foreach (ServiceProviderSettingViewModel item in e.OldItems)
                item.PropertyChanged -= OnMetadataProviderPropertyChanged;

        if (!_isInitializing)
        {
            _dispatcherService.TryEnqueue(() =>
            {
                if (_isDisposed) return;
                var musicBrainz = MetadataProviders.FirstOrDefault(provider => provider.Id == ServiceProviderIds.MusicBrainz);
                var musicBrainzIndex = musicBrainz is null ? -1 : MetadataProviders.IndexOf(musicBrainz);

                if (musicBrainzIndex > 0)
                {
                    ApplyWithoutSaving(() => MetadataProviders.Move(musicBrainzIndex, 0));
                }

                QueueMetadataProviderSave();
            });
        }
    }

    private void OnMetadataProviderPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isInitializing || e.PropertyName != nameof(ServiceProviderSettingViewModel.IsEnabled)) return;

        if (sender is ServiceProviderSettingViewModel provider)
        {
            // If MusicBrainz is disabled, disable dependent services
            if (provider.Id == ServiceProviderIds.MusicBrainz && !provider.IsEnabled)
            {
                var dependents = MetadataProviders.Where(p => p.Id == ServiceProviderIds.TheAudioDb || p.Id == ServiceProviderIds.FanartTv).ToList();
                foreach (var dependent in dependents)
                {
                    dependent.IsEnabled = false;
                }
            }
            // If trying to enable a dependent service while MusicBrainz is disabled, revert it
            else if ((provider.Id == ServiceProviderIds.TheAudioDb || provider.Id == ServiceProviderIds.FanartTv) && provider.IsEnabled)
            {
                var musicBrainz = MetadataProviders.FirstOrDefault(p => p.Id == ServiceProviderIds.MusicBrainz);
                if (musicBrainz is not { IsEnabled: true })
                {
                    provider.IsEnabled = false;
                }
            }
        }

        QueueMetadataProviderSave();
    }

    private void QueueMetadataProviderSave()
    {
        var snapshot = MetadataProviders.Select((vm, i) => vm.ToSetting(i)).ToList();
        QueueSettingsSave(_metadataProviderSaveDebouncer, _ => _settingsService.SetServiceProvidersAsync(ServiceCategory.Metadata, snapshot), nameof(MetadataProviders));
    }

    partial void OnIsFetchOnlineMetadataEnabledChanged(bool value) =>
        _ = SaveSettingAsync(() => _settingsService.SetFetchOnlineMetadataEnabledAsync(value));

    partial void OnIsIgnoreLeadingArticlesOnSortEnabledChanged(bool value) =>
        _ = SaveSettingAsync(() => _settingsService.SetIgnoreLeadingArticlesOnSortEnabledAsync(value));

    partial void OnIsFetchOnlineLyricsEnabledChanged(bool value) =>
        _ = SaveSettingAsync(() => _settingsService.SetFetchOnlineLyricsEnabledAsync(value));

    partial void OnIsLyricsRomanizationEnabledChanged(bool value) =>
        _ = SaveSettingAsync(() => _settingsService.SetLyricsRomanizationEnabledAsync(value));
}

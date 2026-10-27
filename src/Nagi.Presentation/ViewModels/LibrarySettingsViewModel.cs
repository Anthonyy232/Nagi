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
using Nagi.Presentation.Helpers;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.Services;

namespace Nagi.Presentation.ViewModels;

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
    private readonly GlobalOperationViewModel _globalOperation;
    private readonly Debouncer _lyricsProviderSaveDebouncer;
    private readonly Debouncer _metadataProviderSaveDebouncer;
    private readonly Debouncer _artistSplitSaveDebouncer;
    private readonly Debouncer _genreSplitSaveDebouncer;
    private CancellationTokenSource? _metadataRescanCts;

    public LibrarySettingsViewModel(
        IUISettingsService settingsService,
        IUIService uiService,
        ILibraryScanner libraryScanner,
        GlobalOperationViewModel globalOperation,
        IDispatcherService dispatcherService,
        ILogger<LibrarySettingsViewModel> logger,
        TimeProvider? timeProvider = null)
        : base(dispatcherService, logger)
    {
        _settingsService = settingsService;
        _uiService = uiService;
        _libraryScanner = libraryScanner;
        _globalOperation = globalOperation;
        _globalOperation.PropertyChanged += OnGlobalOperationPropertyChanged;
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
                .OrderByDescending(provider => provider.IsPinnedFirst)
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
        _globalOperation.PropertyChanged -= OnGlobalOperationPropertyChanged;
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

    private bool CanRescanMetadata() => !_globalOperation.IsInProgress;

    private void OnGlobalOperationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GlobalOperationViewModel.IsInProgress))
        {
            RescanMetadataCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRescanMetadata))]
    private async Task RescanMetadataAsync()
    {
        if (_globalOperation.IsInProgress) return;

        var confirmed = await _uiService.ShowConfirmationDialogAsync(
            Strings.Settings_Dialog_Rescan_Title,
            Strings.Settings_Dialog_Rescan_Content,
            Strings.Settings_Dialog_Rescan_PrimaryButton,
            null);

        if (!confirmed || _isDisposed || _globalOperation.IsInProgress) return;

        using var rescanCts = new CancellationTokenSource();
        _metadataRescanCts = rescanCts;
        var token = rescanCts.Token;

        _globalOperation.IsInProgress = true;
        _globalOperation.StatusMessage = Strings.Settings_Status_Rescan_Preparing;
        _globalOperation.IsIndeterminate = true;
        _globalOperation.SetCancellation(rescanCts.Cancel);

        try
        {
            await SaveMetadataSplitSettingsAsync();
            token.ThrowIfCancellationRequested();
            await _libraryScanner.ForceRescanMetadataAsync(CreateGlobalOperationProgress(_globalOperation, token), token);
            token.ThrowIfCancellationRequested();

            await _uiService.ShowMessageDialogAsync(Strings.Settings_Dialog_RescanComplete_Title, Strings.Settings_Dialog_RescanComplete_Content);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Metadata rescan cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Metadata rescan failed.");
            await _uiService.ShowMessageDialogAsync(Strings.Settings_Dialog_RescanFailed_Title, Strings.Settings_Dialog_RescanFailed_Content);
        }
        finally
        {
            rescanCts.Cancel();
            _metadataRescanCts = null;
            _globalOperation.SetCancellation(null);
            _globalOperation.IsInProgress = false;
            _globalOperation.IsIndeterminate = false;
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
                var pinned = MetadataProviders.FirstOrDefault(provider => provider.IsPinnedFirst);
                var pinnedIndex = pinned is null ? -1 : MetadataProviders.IndexOf(pinned);

                if (pinnedIndex > 0)
                {
                    ApplyWithoutSaving(() => MetadataProviders.Move(pinnedIndex, 0));
                }

                QueueMetadataProviderSave();
            });
        }
    }

    private void OnMetadataProviderPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isInitializing || e.PropertyName != nameof(ServiceProviderSettingViewModel.IsEnabled)) return;
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

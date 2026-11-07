using System.Collections.Specialized;
using System.ComponentModel;
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
using Nagi.Core.Settings;

namespace Nagi.Presentation.ViewModels;

/// <summary>
///     Settings section for how the library is read and enriched: title sorting, artist and genre
///     splitting, metadata rescans, and the online metadata and lyrics providers.
/// </summary>
public partial class LibrarySettingsViewModel : SettingsSectionViewModel
{
    private const int SettingsSaveDebounceMs = 300;

    private readonly IUIService _uiService;
    private readonly ILibraryFolderService _libraryFolders;
    private readonly IMusicServerService _musicServers;
    private readonly GlobalOperationViewModel _globalOperation;
    private readonly Debouncer _lyricsProviderSaveDebouncer;
    private readonly Debouncer _metadataProviderSaveDebouncer;
    private CancellationTokenSource? _metadataRescanCts;

    public LibrarySettingsViewModel(
        ISettingsService settingsService,
        IUIService uiService,
        ILibraryFolderService libraryFolders,
        IMusicServerService musicServers,
        GlobalOperationViewModel globalOperation,
        IDispatcherService dispatcherService,
        ILogger<LibrarySettingsViewModel> logger,
        TimeProvider? timeProvider = null)
        : base(settingsService, dispatcherService, logger)
    {
        _uiService = uiService;
        _libraryFolders = libraryFolders;
        _musicServers = musicServers;
        _globalOperation = globalOperation;
        _globalOperation.PropertyChanged += OnGlobalOperationPropertyChanged;
        timeProvider ??= TimeProvider.System;
        _lyricsProviderSaveDebouncer = new Debouncer(TimeSpan.FromMilliseconds(SettingsSaveDebounceMs), timeProvider);
        _metadataProviderSaveDebouncer = new Debouncer(TimeSpan.FromMilliseconds(SettingsSaveDebounceMs), timeProvider);

        LyricsProviders.CollectionChanged += OnLyricsProvidersCollectionChanged;
        MetadataProviders.CollectionChanged += OnMetadataProvidersCollectionChanged;
    }

    public bool IsFetchOnlineMetadataEnabled
    {
        get => Get(CoreSettings.FetchOnlineMetadata);
        set => Set(CoreSettings.FetchOnlineMetadata, value);
    }

    public bool IsIgnoreLeadingArticlesOnSortEnabled
    {
        get => Get(CoreSettings.IgnoreLeadingArticlesOnSort);
        set => Set(CoreSettings.IgnoreLeadingArticlesOnSort, value);
    }

    public bool IsFetchOnlineLyricsEnabled
    {
        get => Get(CoreSettings.FetchOnlineLyrics);
        set => Set(CoreSettings.FetchOnlineLyrics, value);
    }

    public bool IsLyricsRomanizationEnabled
    {
        get => Get(CoreSettings.LyricsRomanization);
        set => Set(CoreSettings.LyricsRomanization, value);
    }

    public string ArtistSplitCharacters
    {
        get => Get(CoreSettings.ArtistSplitCharacters);
        set => Set(CoreSettings.ArtistSplitCharacters, value);
    }

    public string GenreSplitCharacters
    {
        get => Get(CoreSettings.GenreSplitCharacters);
        set => Set(CoreSettings.GenreSplitCharacters, value);
    }

    public ObservableRangeCollection<ServiceProviderSettingViewModel> LyricsProviders { get; } = new();
    public ObservableRangeCollection<ServiceProviderSettingViewModel> MetadataProviders { get; } = new();

    internal Task PendingSettingsSaves => Task.WhenAll(
        _lyricsProviderSaveDebouncer.PendingInvocation,
        _metadataProviderSaveDebouncer.PendingInvocation);

    /// <summary>
    ///     Loads the provider lists without writing anything back.
    /// </summary>
    public void Load()
    {
        ApplyWithoutSaving(() =>
        {
            foreach (var item in LyricsProviders) item.PropertyChanged -= OnLyricsProviderPropertyChanged;
            foreach (var item in MetadataProviders) item.PropertyChanged -= OnMetadataProviderPropertyChanged;

            var lyricsMapped = _settingsService.Get(CoreSettings.LyricsProviders)
                .Select(ServiceProviderSettingViewModel.FromSetting)
                .ToList();
            foreach (var item in lyricsMapped)
                item.PropertyChanged += OnLyricsProviderPropertyChanged;
            LyricsProviders.ReplaceRange(lyricsMapped);

            var metadataProviders = _settingsService.Get(CoreSettings.MetadataProviders)
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
            var progress = _globalOperation.CreateProgress(token);
            await _libraryFolders.ForceRescanMetadataAsync(progress, token);
            token.ThrowIfCancellationRequested();
            // A sync applies the split settings to server songs.
            await _musicServers.SyncAllAsync(progress, token);
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
        QueueSettingsSave(_lyricsProviderSaveDebouncer, _ =>
        {
            Set(CoreSettings.LyricsProviders, snapshot, nameof(LyricsProviders));
            return Task.CompletedTask;
        }, nameof(LyricsProviders));
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
            // Deferred: moving the pinned provider here would change the collection while it is still raising this event.
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
        QueueSettingsSave(_metadataProviderSaveDebouncer, _ =>
        {
            Set(CoreSettings.MetadataProviders, snapshot, nameof(MetadataProviders));
            return Task.CompletedTask;
        }, nameof(MetadataProviders));
    }
}

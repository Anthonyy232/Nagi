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
    private readonly IUIService _uiService;
    private readonly ILibraryFolderService _libraryFolders;
    private readonly IMusicServerService _musicServers;
    private readonly GlobalOperationViewModel _globalOperation;
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
        LyricsProviders = new ReorderableToggleList<ServiceProviderSettingViewModel>(
            providers => Set(CoreSettings.LyricsProviders, ToSettings(providers), nameof(LyricsProviders)),
            dispatcherService, timeProvider);
        MetadataProviders = new ReorderableToggleList<ServiceProviderSettingViewModel>(
            providers => Set(CoreSettings.MetadataProviders, ToSettings(providers), nameof(MetadataProviders)),
            dispatcherService, timeProvider, provider => provider.IsPinnedFirst);
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

    public ReorderableToggleList<ServiceProviderSettingViewModel> LyricsProviders { get; }
    public ReorderableToggleList<ServiceProviderSettingViewModel> MetadataProviders { get; }

    internal Task PendingSettingsSaves => Task.WhenAll(
        LyricsProviders.PendingSave,
        MetadataProviders.PendingSave);

    /// <summary>
    ///     Loads the provider lists without writing anything back.
    /// </summary>
    public void Load()
    {
        LyricsProviders.Load(_settingsService.Get(CoreSettings.LyricsProviders)
            .Select(ServiceProviderSettingViewModel.FromSetting));
        MetadataProviders.Load(_settingsService.Get(CoreSettings.MetadataProviders)
            .Select(ServiceProviderSettingViewModel.FromSetting));
    }

    protected override void OnDisposing()
    {
        LyricsProviders.Dispose();
        MetadataProviders.Dispose();
        _metadataRescanCts?.Cancel();
        _globalOperation.PropertyChanged -= OnGlobalOperationPropertyChanged;
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

        using (_globalOperation.Begin(Strings.Settings_Status_Rescan_Preparing, rescanCts.Cancel))
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
            }
    }

    private static List<ServiceProviderSetting> ToSettings(IReadOnlyList<ServiceProviderSettingViewModel> providers) =>
        providers.Select((provider, order) => provider.ToSetting(order)).ToList();
}

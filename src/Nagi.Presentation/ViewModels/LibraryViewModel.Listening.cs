using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nagi.Core.Models;
using Nagi.Presentation.Resources;

namespace Nagi.Presentation.ViewModels;

public partial class LibraryViewModel
{
    private bool _suggestionDismissed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSuggestion), nameof(EmptyStateTitle), nameof(EmptyStateSubtitle))]
    public partial bool ShowFavoritesOnly { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSuggestion))]
    [NotifyPropertyChangedFor(nameof(SuggestionReason))]
    public partial RediscoverySuggestion? Suggestion { get; set; }

    public bool ShowSuggestion => Suggestion is not null && !ShowFavoritesOnly && !IsSearchActive;
    public string SuggestionReason => Suggestion is null ? string.Empty
        : string.Format(Strings.Listening_RediscoveryReason, Suggestion.LastPlayedDate.ToLocalTime());

    public string EmptyStateTitle => IsSearchActive
        ? Strings.LibraryPage_SearchEmpty_Title
        : ShowFavoritesOnly ? Strings.Listening_FavoritesEmpty : Strings.LibraryPage_EmptyState_Title;

    public string EmptyStateSubtitle => IsSearchActive
        ? Strings.LibraryPage_SearchEmpty_Subtitle
        : ShowFavoritesOnly ? Strings.Listening_FavoritesEmptyDescription : Strings.LibraryPage_EmptyState_Subtitle;

    /// <summary>
    ///     Shows the whole library or only favorites, loading the songs and starting the initial background rescan on
    ///     the first visit.
    /// </summary>
    public async Task OpenAsync(bool favorites)
    {
        await SetLibraryViewAsync(favorites);
        if (!HasInitialized) await InitializeAsync();
    }

    public async Task SetLibraryViewAsync(bool favorites)
    {
        if (ShowFavoritesOnly == favorites) return;
        _pagedLoadCts?.Cancel();
        ShowFavoritesOnly = favorites;
        PageTitle = favorites ? Strings.Listening_Favorites : Strings.LibraryPage_Title;
        SearchTerm = string.Empty;
        DeselectAll();
        ClearFullSongIds();
        CurrentPage = 1;
        if (HasInitialized) await RefreshOrSortSongsAsync();
    }

    protected override void OnSearchTermChangedInternal(string value)
    {
        base.OnSearchTermChangedInternal(value);
        OnPropertyChanged(nameof(ShowSuggestion));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateSubtitle));
    }

    protected override async Task OnAuxiliaryLoadAsync(CancellationToken token)
    {
        await base.OnAuxiliaryLoadAsync(token);
        if (!_suggestionDismissed && !ShowFavoritesOnly && !IsSearchActive)
        {
            try
            {
                var suggestion = await _libraryReader.GetRediscoverySuggestionAsync(token);
                await _dispatcherService.EnqueueAsync(() =>
                {
                    if (!token.IsCancellationRequested && !_isDisposed && !_suggestionDismissed) Suggestion = suggestion;
                    return Task.CompletedTask;
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to load rediscovery suggestion");
            }
        }
    }

    [RelayCommand]
    private Task PlaySuggestionAsync()
    {
        var suggestion = Suggestion;
        if (suggestion is null) return Task.CompletedTask;
        return RunActionAsync(async () =>
        {
            await _playbackService.PlayAsync(suggestion.SongId);
            DismissSuggestion();
        });
    }

    [RelayCommand]
    private void DismissSuggestion()
    {
        _suggestionDismissed = true;
        Suggestion = null;
    }
}

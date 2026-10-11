using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nagi.Core.Models;
using Nagi.WinUI.Resources;

namespace Nagi.WinUI.ViewModels;

public partial class LibraryViewModel
{
    private bool _suggestionDismissed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSuggestion))]
    public partial bool ShowFavoritesOnly { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSuggestion))]
    [NotifyPropertyChangedFor(nameof(SuggestionReason))]
    public partial RediscoverySuggestion? Suggestion { get; set; }

    public bool ShowSuggestion => Suggestion is not null && !ShowFavoritesOnly && !IsSearchActive;
    public string SuggestionReason => Suggestion is null ? string.Empty
        : string.Format(Strings.Listening_RediscoveryReason, Suggestion.LastPlayedDate.ToLocalTime());

    partial void OnShowFavoritesOnlyChanged(bool value)
    {
        _pagedLoadCts?.Cancel();
        DeselectAll();
        ClearFullSongIds();
        CurrentPage = 1;
        _ = RefreshOrSortSongsAsync();
    }

    protected override void OnSearchTermChangedInternal(string value)
    {
        base.OnSearchTermChangedInternal(value);
        OnPropertyChanged(nameof(ShowSuggestion));
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
        return RunListeningActionAsync(async () =>
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

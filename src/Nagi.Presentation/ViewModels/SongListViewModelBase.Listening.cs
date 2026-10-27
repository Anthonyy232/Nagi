using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Nagi.Core.Models;
using Nagi.Presentation.Helpers;
using Nagi.Presentation.Resources;

namespace Nagi.Presentation.ViewModels;

public abstract partial class SongListViewModelBase
{
    [RelayCommand(CanExecute = nameof(CanExecutePlayAllCommands))]
    private Task PlayMixAsync(ShuffleMode mode) => RunActionAsync(async () =>
    {
        var generation = CaptureSongContext();
        var context = GetPlaybackContext();
        var ids = await GetFullSongIdsAsync();
        if (!IsSongContextCurrent(generation) || ids.Count == 0) return;
        if (!await _playbackService.PlayMixAsync(ids, mode, context: context))
            await _uiService.ShowMessageDialogAsync(Strings.Listening_NoMixTitle, ShuffleModeText.GetEmptyMixDescription(mode));
    });

    [RelayCommand(CanExecute = nameof(CanExecutePlayAllCommands))]
    private Task PlayForAsync() => RunActionAsync(async () =>
    {
        var generation = CaptureSongContext();
        var context = GetPlaybackContext();
        var options = await _uiService.ShowPlayForDialogAsync();
        if (options is null || !IsSongContextCurrent(generation)) return;
        var ids = await GetFullSongIdsAsync();
        if (!IsSongContextCurrent(generation) || ids.Count == 0) return;
        if (!await _playbackService.PlayMixAsync(ids, options.Mode, TimeSpan.FromMinutes(options.Minutes), context, options.AvoidRecentDays))
            await _uiService.ShowMessageDialogAsync(Strings.Listening_NoMixTitle, ShuffleModeText.GetEmptyMixDescription(options.Mode, timed: true));
    });

    [RelayCommand(CanExecute = nameof(CanExecuteSelectedSongsCommands))]
    private Task SetSelectedFavoritesAsync(bool loved) => RunActionAsync(async () =>
    {
        var ids = await GetCurrentSelectionIdsAsync();
        if (ids.Count == 0) return;
        if (await Task.Run(() => _libraryWriter.SetSongsLovedAsync(ids, loved)))
        {
            var changed = ids.ToHashSet();
            foreach (var song in Songs.Concat(SelectedSongs).DistinctBy(song => song.Id))
                if (changed.Contains(song.Id)) song.IsLoved = loved;
        }
    });

    /// <summary>The favorite action for the one selected song.</summary>
    public string SelectedFavoriteActionText =>
        SelectedSongs.FirstOrDefault()?.IsLoved == true ? Strings.Listening_RemoveFavorite : Strings.Listening_AddFavorite;

    [RelayCommand(CanExecute = nameof(CanExecuteSingleSongCommands))]
    private async Task ToggleSelectedFavoriteAsync()
    {
        if (SelectedSongs.Count == 1) await SetSelectedFavoritesAsync(!SelectedSongs[0].IsLoved);
    }
}

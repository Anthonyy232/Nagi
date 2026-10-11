using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.WinUI.Resources;

namespace Nagi.WinUI.ViewModels;

public abstract partial class SongListViewModelBase
{
    [RelayCommand(CanExecute = nameof(CanExecutePlayAllCommands))]
    private Task PlayMixAsync(ShuffleMode mode) => RunListeningActionAsync(async () =>
    {
        var generation = CaptureSongContext();
        var context = GetPlaybackContext();
        var ids = await GetFullSongIdsAsync();
        if (!IsSongContextCurrent(generation) || ids.Count == 0) return;
        if (!await _playbackService.PlayMixAsync(ids, mode, context: context))
            await _uiService.ShowMessageDialogAsync(Strings.Listening_NoMixTitle, Strings.Listening_NoMixDescription);
    });

    [RelayCommand(CanExecute = nameof(CanExecutePlayAllCommands))]
    private Task PlayForAsync() => RunListeningActionAsync(async () =>
    {
        var generation = CaptureSongContext();
        var context = GetPlaybackContext();
        var options = await _uiService.ShowPlayForDialogAsync();
        if (options is null || !IsSongContextCurrent(generation)) return;
        var ids = await GetFullSongIdsAsync();
        if (!IsSongContextCurrent(generation) || ids.Count == 0) return;
        if (!await _playbackService.PlayMixAsync(ids, options.Mode, TimeSpan.FromMinutes(options.Minutes), context, options.AvoidRecentDays))
            await _uiService.ShowMessageDialogAsync(Strings.Listening_NoMixTitle, Strings.Listening_NoMixDescription);
    });

    protected async Task RunListeningActionAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Listening action failed");
            if (!_isDisposed) await _uiService.ShowMessageDialogAsync(Strings.Generic_Error, Strings.Listening_ActionError);
        }
    }
}

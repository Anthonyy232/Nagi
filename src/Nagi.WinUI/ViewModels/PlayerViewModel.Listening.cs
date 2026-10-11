using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Data;
using Nagi.WinUI.Models;
using Nagi.WinUI.Resources;
using Nagi.WinUI.Services.Abstractions;
using Nagi.WinUI.Services.Implementations;

namespace Nagi.WinUI.ViewModels;

public partial class PlayerViewModel
{
    private readonly IUIService _uiService;

    [RelayCommand]
    private Task PlayQueueMixAsync(ShuffleMode mode) => RunListeningActionAsync(async () =>
    {
        if (!await _playbackService.PlayMixAsync(_playbackService.PlaybackQueue, mode))
            await _uiService.ShowMessageDialogAsync(Strings.Listening_NoMixTitle, Strings.Listening_NoMixDescription);
    });

    private async Task RunListeningActionAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Listening action failed");
            if (!_isCleanedUp) await _uiService.ShowMessageDialogAsync(Strings.Generic_Error, Strings.Listening_ActionError);
        }
    }
}

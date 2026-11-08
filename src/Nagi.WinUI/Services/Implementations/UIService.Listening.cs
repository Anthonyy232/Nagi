using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Nagi.Presentation.Common;
using Nagi.Presentation.Player;
using Nagi.WinUI.Dialogs;

namespace Nagi.WinUI.Services.Implementations;

public partial class UIService
{
    public async Task<PlaybackMixOptions?> ShowPlayForDialogAsync()
    {
        if (!TryGetXamlRoot(out var xamlRoot)) return null;

        var viewModel = new PlayForDialogViewModel();
        var result = await ShowDialogAsync(new PlayForDialog(viewModel) { XamlRoot = xamlRoot });
        return result == ContentDialogResult.Primary ? viewModel.ToOptions() : null;
    }

    public async Task<(int Minutes, bool FinishTrack)?> ShowSleepTimerDialogAsync()
    {
        if (!TryGetXamlRoot(out var xamlRoot)) return null;

        var viewModel = new SleepTimerDialogViewModel();
        var result = await ShowDialogAsync(new SleepTimerDialog(viewModel) { XamlRoot = xamlRoot });
        return result == ContentDialogResult.Primary ? ((int)viewModel.Minutes, viewModel.FinishTrack) : null;
    }
}

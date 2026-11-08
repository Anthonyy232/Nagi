using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Nagi.Core.Metadata;
using Nagi.Presentation.Playlists;
using Nagi.WinUI.Dialogs;

namespace Nagi.WinUI.Services.Implementations;

public partial class UIService
{
    public async Task<NewPlaylistRequest?> ShowCreatePlaylistDialogAsync()
    {
        var viewModel = new PlaylistNameDialogViewModel();
        return await ShowPlaylistNameDialogAsync(viewModel)
            ? new NewPlaylistRequest(viewModel.Name, viewModel.CoverImagePath)
            : null;
    }

    public async Task<string?> ShowRenamePlaylistDialogAsync(string currentName)
    {
        var viewModel = new PlaylistNameDialogViewModel(currentName);
        return await ShowPlaylistNameDialogAsync(viewModel) ? viewModel.Name : null;
    }

    private async Task<bool> ShowPlaylistNameDialogAsync(PlaylistNameDialogViewModel viewModel)
    {
        if (!TryGetXamlRoot(out var xamlRoot)) return false;

        var dialog = new PlaylistNameDialog(viewModel, () => PickSingleFileAsync(FileExtensions.ImageFileExtensions))
        {
            XamlRoot = xamlRoot
        };
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }
}

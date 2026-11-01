using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Nagi.Core.Models;
using Nagi.Presentation.ViewModels;
using Nagi.WinUI.Dialogs;

namespace Nagi.WinUI.Services.Implementations;

public partial class UIService
{
    public async Task<Folder?> ShowAddMusicServerDialogAsync()
    {
        if (!TryGetXamlRoot(out var xamlRoot)) return null;

        var viewModel = _viewModelFactory.Create<MusicServerDialogViewModel>();
        var result = await ShowDialogAsync(new MusicServerDialog(viewModel) { XamlRoot = xamlRoot });
        return result == ContentDialogResult.Primary ? viewModel.AddedServer : null;
    }

    public async Task<bool> ShowMusicServerSignInDialogAsync(Guid folderId)
    {
        if (!TryGetXamlRoot(out var xamlRoot)) return false;

        var viewModel = _viewModelFactory.Create<MusicServerDialogViewModel>();
        if (!await viewModel.InitializeForSignInAsync(folderId)) return false;
        var result = await ShowDialogAsync(new MusicServerDialog(viewModel) { XamlRoot = xamlRoot });
        return result == ContentDialogResult.Primary;
    }
}

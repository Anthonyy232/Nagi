using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Nagi.Core.Library;
using Nagi.Presentation.MusicServers;
using Nagi.WinUI.Dialogs;

namespace Nagi.WinUI.Services.Implementations;

public partial class UIService
{
    public async Task<Folder?> ShowAddMusicServerDialogAsync()
    {
        if (!TryGetXamlRoot(out var xamlRoot)) return null;

        using var scope = _scopeFactory.CreateScope();
        var viewModel = scope.ServiceProvider.GetRequiredService<MusicServerDialogViewModel>();
        var result = await ShowDialogAsync(new MusicServerDialog(viewModel) { XamlRoot = xamlRoot });
        return result == ContentDialogResult.Primary ? viewModel.AddedServer : null;
    }

    public async Task<bool> ShowMusicServerSignInDialogAsync(Guid folderId)
    {
        if (!TryGetXamlRoot(out var xamlRoot)) return false;

        using var scope = _scopeFactory.CreateScope();
        var viewModel = scope.ServiceProvider.GetRequiredService<MusicServerDialogViewModel>();
        if (!await viewModel.InitializeForSignInAsync(folderId)) return false;
        var result = await ShowDialogAsync(new MusicServerDialog(viewModel) { XamlRoot = xamlRoot });
        return result == ContentDialogResult.Primary;
    }
}

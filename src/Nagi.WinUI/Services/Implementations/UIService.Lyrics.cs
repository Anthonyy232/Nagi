using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Nagi.Presentation.Common;
using Nagi.Presentation.Lyrics;
using Nagi.WinUI.Dialogs;

namespace Nagi.WinUI.Services.Implementations;

public partial class UIService
{
    public async Task<LyricsEdit?> ShowLyricsEditorAsync(string lyrics, bool canRemove)
    {
        if (!TryGetXamlRoot(out var xamlRoot)) return null;

        var viewModel = new LyricsEditorDialogViewModel(lyrics, canRemove);
        var dialog = new LyricsEditorDialog(viewModel) { XamlRoot = xamlRoot };
        return await ShowDialogAsync(dialog) switch
        {
            ContentDialogResult.Primary => viewModel.Save(),
            ContentDialogResult.Secondary => new LyricsEdit(LyricsEditAction.Remove),
            _ => null
        };
    }
}

using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.Presentation.Common;
using Nagi.Presentation.Lyrics;
using Nagi.Presentation.Resources;

namespace Nagi.WinUI.Services.Implementations;

public partial class UIService
{
    public async Task<LyricsEdit?> ShowLyricsEditorAsync(string lyrics, bool canRemove)
    {
        if (!TryGetXamlRoot(out var xamlRoot)) return null;

        var viewModel = new LyricsEditorDialogViewModel(lyrics, canRemove);
        var editor = new TextBox
        {
            AcceptsReturn = true,
            Text = viewModel.Lyrics,
            PlaceholderText = Strings.LyricsPage_EditLyrics_Placeholder,
            TextWrapping = TextWrapping.Wrap,
            MinWidth = 520,
            Height = 360
        };
        ScrollViewer.SetVerticalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        var dialog = new ContentDialog
        {
            Title = Strings.LyricsPage_EditLyrics_Title,
            Content = editor,
            PrimaryButtonText = Strings.LyricsPage_EditLyrics_Save,
            SecondaryButtonText = viewModel.CanRemove ? Strings.LyricsPage_EditLyrics_Remove : string.Empty,
            CloseButtonText = Strings.Generic_Cancel,
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = viewModel.CanSave,
            XamlRoot = xamlRoot
        };

        editor.TextChanged += (_, _) =>
        {
            viewModel.Lyrics = editor.Text;
            dialog.IsPrimaryButtonEnabled = viewModel.CanSave;
        };
        return await ShowDialogAsync(dialog) switch
        {
            ContentDialogResult.Primary => viewModel.Save(),
            ContentDialogResult.Secondary => new LyricsEdit(LyricsEditAction.Remove),
            _ => null
        };
    }
}

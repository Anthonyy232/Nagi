using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.Presentation.Models;
using Nagi.Presentation.Resources;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.Services.Implementations;

public partial class UIService
{
    public async Task<LyricsEdit?> ShowLyricsEditorAsync(string lyrics, bool canRemove)
    {
        if (!TryGetXamlRoot(out var xamlRoot)) return null;

        var editor = new TextBox
        {
            AcceptsReturn = true,
            Text = lyrics,
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
            SecondaryButtonText = canRemove ? Strings.LyricsPage_EditLyrics_Remove : string.Empty,
            CloseButtonText = Strings.Generic_Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot
        };

        void UpdateSaveButton() => dialog.IsPrimaryButtonEnabled = LyricsEdit.CanSave(editor.Text, lyrics);

        editor.TextChanged += (_, _) => UpdateSaveButton();
        UpdateSaveButton();
        return await ShowDialogAsync(dialog) switch
        {
            ContentDialogResult.Primary => new LyricsEdit(LyricsEditAction.Save, editor.Text),
            ContentDialogResult.Secondary => new LyricsEdit(LyricsEditAction.Remove),
            _ => null
        };
    }
}

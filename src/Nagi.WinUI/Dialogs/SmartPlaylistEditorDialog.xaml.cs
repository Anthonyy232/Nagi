using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.Presentation.Common;
using Nagi.Presentation.Playlists;
using Nagi.Presentation.Resources;

namespace Nagi.WinUI.Dialogs;

/// <summary>
///     The view for <see cref="SmartPlaylistEditorViewModel" />: creates or edits a smart playlist.
/// </summary>
public sealed partial class SmartPlaylistEditorDialog : ContentDialog
{
    public SmartPlaylistEditorDialog(SmartPlaylistEditorViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

    }

    public SmartPlaylistEditorViewModel ViewModel { get; }

    private void RemoveRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: RuleViewModel rule }) ViewModel.RemoveRuleCommand.Execute(rule);
    }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var result = await ViewModel.SaveAsync();
            if (result == SmartPlaylistSaveResult.Saved) return;

            args.Cancel = true;
            if (result == SmartPlaylistSaveResult.NameMissing)
                PlaylistNameTextBox.PlaceholderText = Strings.SmartPlaylist_Error_NameEmptyFeedback;
            if (result is SmartPlaylistSaveResult.NameMissing or SmartPlaylistSaveResult.DuplicateName)
                PlaylistNameTextBox.Focus(FocusState.Programmatic);
        }
        finally
        {
            deferral.Complete();
        }
    }
}

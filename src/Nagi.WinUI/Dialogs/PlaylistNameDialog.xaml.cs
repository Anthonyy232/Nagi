using Microsoft.UI.Xaml.Controls;
using Nagi.Presentation.Playlists;

namespace Nagi.WinUI.Dialogs;

public sealed partial class PlaylistNameDialog : ContentDialog
{
    public PlaylistNameDialog(PlaylistNameDialogViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public PlaylistNameDialogViewModel ViewModel { get; }
}

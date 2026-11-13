using Microsoft.UI.Xaml.Controls;
using Nagi.Presentation.Lyrics;

namespace Nagi.WinUI.Dialogs;

public sealed partial class LyricsEditorDialog : ContentDialog
{
    public LyricsEditorDialog(LyricsEditorDialogViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public LyricsEditorDialogViewModel ViewModel { get; }
}

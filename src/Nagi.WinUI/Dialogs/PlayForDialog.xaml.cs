using Microsoft.UI.Xaml.Controls;
using Nagi.Presentation.Player;

namespace Nagi.WinUI.Dialogs;

public sealed partial class PlayForDialog : ContentDialog
{
    public PlayForDialog(PlayForDialogViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public PlayForDialogViewModel ViewModel { get; }
}

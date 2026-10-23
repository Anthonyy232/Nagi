using Microsoft.UI.Xaml.Controls;
using Nagi.Presentation.ViewModels;

namespace Nagi.WinUI.Dialogs;

public sealed partial class SongInfoDialog : ContentDialog
{
    public SongInfoDialog(SongInfoViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public SongInfoViewModel ViewModel { get; }
}

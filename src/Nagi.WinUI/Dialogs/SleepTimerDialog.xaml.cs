using Microsoft.UI.Xaml.Controls;
using Nagi.Presentation.Player;

namespace Nagi.WinUI.Dialogs;

public sealed partial class SleepTimerDialog : ContentDialog
{
    public SleepTimerDialog(SleepTimerDialogViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public SleepTimerDialogViewModel ViewModel { get; }
}

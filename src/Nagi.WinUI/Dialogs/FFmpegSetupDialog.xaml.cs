using Microsoft.UI.Xaml.Controls;
using Nagi.Presentation.Settings;

namespace Nagi.WinUI.Dialogs;

public sealed partial class FFmpegSetupDialog : ContentDialog
{
    public FFmpegSetupDialog(FFmpegSetupViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public FFmpegSetupViewModel ViewModel { get; }

    // Stays open until a recheck finds FFmpeg.
    private async void OnRecheckClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        args.Cancel = !await ViewModel.RecheckAsync();
        deferral.Complete();
    }
}

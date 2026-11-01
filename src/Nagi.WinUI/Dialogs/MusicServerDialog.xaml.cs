using Microsoft.UI.Xaml.Controls;
using Nagi.Presentation.ViewModels;

namespace Nagi.WinUI.Dialogs;

/// <summary>
///     The view for <see cref="MusicServerDialogViewModel" />: adds a music server, or signs in to one again.
/// </summary>
public sealed partial class MusicServerDialog : ContentDialog
{
    public MusicServerDialog(MusicServerDialogViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public MusicServerDialogViewModel ViewModel { get; }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            args.Cancel = !await ViewModel.SubmitAsync();
        }
        finally
        {
            deferral.Complete();
        }
    }

    // Cancelling mid-check would close the dialog while the server could still be added, leaving it unsynced.
    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (ViewModel.IsBusy) args.Cancel = true;
    }
}

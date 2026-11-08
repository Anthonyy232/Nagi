using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.Presentation.Playlists;

namespace Nagi.WinUI.Dialogs;

public sealed partial class PlaylistNameDialog : ContentDialog
{
    private readonly Func<Task<string?>> _pickImage;

    /// <param name="pickImage">Asks for a cover image file; returns null when the user cancels.</param>
    public PlaylistNameDialog(PlaylistNameDialogViewModel viewModel, Func<Task<string?>> pickImage)
    {
        ViewModel = viewModel;
        _pickImage = pickImage;
        InitializeComponent();
    }

    public PlaylistNameDialogViewModel ViewModel { get; }

    private async void PickImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (await _pickImage() is { } path && !string.IsNullOrWhiteSpace(path)) ViewModel.CoverImagePath = path;
    }
}

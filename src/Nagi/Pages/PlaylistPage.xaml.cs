using System;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Nagi.Navigation;
using Nagi.ViewModels;
using WinRT.Interop;

namespace Nagi.Pages;

/// <summary>
///     A page that displays a grid of playlists and allows creating, renaming, and deleting them.
/// </summary>
public sealed partial class PlaylistPage : Page
{
    public PlaylistPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<PlaylistViewModel>();
        DataContext = ViewModel;
    }

    /// <summary>
    ///     Gets the ViewModel for this page.
    /// </summary>
    public PlaylistViewModel ViewModel { get; }

    /// <summary>
    ///     Loads the playlists from the ViewModel when the page is loaded.
    /// </summary>
    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadPlaylistsCommand.ExecuteAsync(null);
    }

    /// <summary>
    ///     Handles clicks on a playlist item, navigating to the song list for that playlist.
    /// </summary>
    private void PlaylistsGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PlaylistViewModelItem clickedPlaylist)
        {
            var navParam = new PlaylistSongViewNavigationParameter
            {
                Title = clickedPlaylist.Name,
                PlaylistId = clickedPlaylist.Id
            };
            Frame.Navigate(typeof(PlaylistSongViewPage), navParam);
        }
    }

    /// <summary>
    ///     Shows a dialog to create a new playlist.
    /// </summary>
    private async void CreateNewPlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsAnyOperationInProgress) return;

        string? selectedCoverImageUriForDialog = null;

        // Programmatically create the content for the dialog.
        var inputTextBox = new TextBox { PlaceholderText = "Enter new playlist name" };
        var imagePreview = new Image { Stretch = Stretch.UniformToFill };
        var imagePlaceholder = new FontIcon { Glyph = "\uE91B", FontSize = 48 };
        var imageGrid = new Grid
        {
            Width = 80,
            Height = 80,
            Margin = new Thickness(0, 0, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        imageGrid.Children.Add(imagePlaceholder);
        imageGrid.Children.Add(imagePreview);

        var pickImageButton = new Button
        {
            Content = "Pick Cover Image",
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var dialogContent = new StackPanel();
        dialogContent.Children.Add(imageGrid);
        dialogContent.Children.Add(inputTextBox);
        dialogContent.Children.Add(pickImageButton);

        var dialog = new ContentDialog
        {
            Title = "Create New Playlist",
            Content = dialogContent,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        // Wire up event handlers for the dialog's controls.
        pickImageButton.Click += async (s, args) =>
        {
            var pickedUri = await PickCoverImageAsync();
            if (!string.IsNullOrWhiteSpace(pickedUri))
            {
                selectedCoverImageUriForDialog = pickedUri;
                imagePreview.Source = new BitmapImage(new Uri(selectedCoverImageUriForDialog));
                imagePlaceholder.Visibility = Visibility.Collapsed;
            }
        };
        inputTextBox.TextChanged += (s, args) =>
            dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(inputTextBox.Text);
        dialog.IsPrimaryButtonEnabled = false;

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            var argsTuple = new Tuple<string, string?>(inputTextBox.Text, selectedCoverImageUriForDialog);
            await ViewModel.CreatePlaylistCommand.ExecuteAsync(argsTuple);
        }
    }

    /// <summary>
    ///     Shows a dialog to rename an existing playlist.
    /// </summary>
    private async void RenamePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PlaylistViewModelItem playlistItem } ||
            ViewModel.IsAnyOperationInProgress) return;

        var inputTextBox = new TextBox { Text = playlistItem.Name };
        var dialog = new ContentDialog
        {
            Title = $"Rename '{playlistItem.Name}'",
            Content = inputTextBox,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        inputTextBox.TextChanged += (s, args) =>
            dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(inputTextBox.Text) &&
                                            inputTextBox.Text.Trim() != playlistItem.Name;
        dialog.IsPrimaryButtonEnabled = false;

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            var argsTuple = new Tuple<Guid, string>(playlistItem.Id, inputTextBox.Text);
            await ViewModel.RenamePlaylistCommand.ExecuteAsync(argsTuple);
        }
    }

    /// <summary>
    ///     Shows a confirmation dialog before deleting a playlist.
    /// </summary>
    private async void DeletePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PlaylistViewModelItem playlistItem } ||
            ViewModel.IsAnyOperationInProgress) return;

        var dialog = new ContentDialog
        {
            Title = "Delete Playlist",
            Content =
                $"Are you sure you want to delete the playlist '{playlistItem.Name}'? This action cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary) await ViewModel.DeletePlaylistCommand.ExecuteAsync(playlistItem.Id);
    }

    /// <summary>
    ///     Handles the click event to change a playlist's cover image.
    /// </summary>
    private async void ChangeCover_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PlaylistViewModelItem playlistItem } ||
            ViewModel.IsAnyOperationInProgress) return;

        var newCoverImageUri = await PickCoverImageAsync();

        if (!string.IsNullOrWhiteSpace(newCoverImageUri))
        {
            var argsTuple = new Tuple<Guid, string>(playlistItem.Id, newCoverImageUri);
            await ViewModel.UpdatePlaylistCoverCommand.ExecuteAsync(argsTuple);
        }
    }

    /// <summary>
    ///     Opens a file picker to select a cover image.
    /// </summary>
    /// <returns>The path to the selected image file, or null if no file was selected.</returns>
    private async Task<string?> PickCoverImageAsync()
    {
        var picker = new FileOpenPicker();
        var hwnd = WindowNative.GetWindowHandle(App.RootWindow);
        InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".png");

        var file = await picker.PickSingleFileAsync();
        if (file != null)
            // Storing the direct file path is not robust, as the app may lose access permissions.
            // For a production app, it is recommended to copy the selected file to the app's
            // local storage and save the path to the copied file instead.
            return file.Path;
        return null;
    }
}
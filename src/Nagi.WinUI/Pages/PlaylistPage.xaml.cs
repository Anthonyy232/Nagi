using System;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using ImageEx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Nagi.WinUI.ViewModels;
using WinRT.Interop;

namespace Nagi.WinUI.Pages;

/// <summary>
///     A page that displays a grid of playlists and allows creating, renaming, and deleting them.
/// </summary>
public sealed partial class PlaylistPage : Page
{
    private readonly ILogger<PlaylistPage> _logger;

    public PlaylistPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<PlaylistViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<PlaylistPage>>();
        DataContext = ViewModel;
        _logger.LogInformation("PlaylistPage initialized.");
    }

    public PlaylistViewModel ViewModel { get; }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("PlaylistPage loaded. Loading playlists...");
        await ViewModel.LoadPlaylistsCommand.ExecuteAsync(null);
        _logger.LogInformation("Finished loading playlists.");
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _logger.LogInformation("Navigating away from PlaylistPage. Disposing ViewModel.");
        ViewModel.Dispose();
    }

    private async void PlayPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PlaylistViewModelItem playlistItem } ||
            ViewModel.IsAnyOperationInProgress) return;

        _logger.LogInformation("User initiated playback of playlist '{PlaylistName}' (Id: {PlaylistId}).",
            playlistItem.Name, playlistItem.Id);
        await ViewModel.PlayPlaylistCommand.ExecuteAsync(new Tuple<Guid, bool>(playlistItem.Id, playlistItem.IsSmart));
    }

    private void PlaylistsGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PlaylistViewModelItem clickedPlaylist)
        {
            _logger.LogInformation(
                "User clicked on playlist '{PlaylistName}' (Id: {PlaylistId}). Navigating to detail view.",
                clickedPlaylist.Name, clickedPlaylist.Id);
            ViewModel.NavigateToPlaylistDetail(clickedPlaylist);
        }
    }

    private async void CreateNewPlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsAnyOperationInProgress)
        {
            _logger.LogDebug("Create playlist button clicked, but an operation is already in progress. Ignoring.");
            return;
        }

        _logger.LogInformation("Showing 'Create New Playlist' dialog.");
        string? selectedCoverImageUriForDialog = null;

        var inputTextBox = new TextBox { PlaceholderText = "Enter new playlist name" };
        var imagePreview = new ImageEx.ImageEx { Stretch = Stretch.UniformToFill, IsCacheEnabled = true };
        var imagePlaceholder = new FontIcon { Glyph = "\uE91B", FontSize = 48 };
        var imageGrid = new Grid
        {
            Width = 80, Height = 80, Margin = new Thickness(0, 0, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        imageGrid.Children.Add(imagePlaceholder);
        imageGrid.Children.Add(imagePreview);
        var pickImageButton = new Button
        {
            Content = "Pick Cover Image", Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var dialogContent = new StackPanel();
        dialogContent.Children.Add(imageGrid);
        dialogContent.Children.Add(inputTextBox);
        dialogContent.Children.Add(pickImageButton);

        var dialog = new ContentDialog
        {
            Title = "Create New Playlist", Content = dialogContent, PrimaryButtonText = "Create",
            CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot
        };

        pickImageButton.Click += async (s, args) =>
        {
            var pickedUri = await PickCoverImageAsync();
            if (!string.IsNullOrWhiteSpace(pickedUri))
            {
                selectedCoverImageUriForDialog = pickedUri;
                imagePreview.Source = pickedUri;
                imagePlaceholder.Visibility = Visibility.Collapsed;
            }
        };
        inputTextBox.TextChanged += (s, args) =>
            dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(inputTextBox.Text);
        dialog.IsPrimaryButtonEnabled = false;

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            _logger.LogInformation("User confirmed creation of new playlist '{PlaylistName}'.", inputTextBox.Text);
            var argsTuple = new Tuple<string, string?>(inputTextBox.Text, selectedCoverImageUriForDialog);
            await ViewModel.CreatePlaylistCommand.ExecuteAsync(argsTuple);
        }
        else
        {
            _logger.LogInformation("User cancelled 'Create New Playlist' dialog.");
        }
    }

    private async void CreateSmartPlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsAnyOperationInProgress)
        {
            _logger.LogDebug("Create smart playlist button clicked, but an operation is already in progress. Ignoring.");
            return;
        }

        _logger.LogInformation("Smart playlist creation requested. Opening editor dialog.");
        
        var dialog = new Dialogs.SmartPlaylistEditorDialog
        {
            XamlRoot = XamlRoot
        };
        
        var result = await dialog.ShowAsync();
        
        if (result == ContentDialogResult.Primary && dialog.ResultPlaylist != null)
        {
            _logger.LogInformation("User created smart playlist '{PlaylistName}' (Id: {PlaylistId}).",
                dialog.ResultPlaylist.Name, dialog.ResultPlaylist.Id);
            await ViewModel.LoadPlaylistsCommand.ExecuteAsync(null);
        }
        else
        {
            _logger.LogInformation("User cancelled smart playlist creation.");
        }
    }

    private async void RenamePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PlaylistViewModelItem playlistItem } ||
            ViewModel.IsAnyOperationInProgress) return;

        _logger.LogInformation("Showing 'Rename Playlist' dialog for '{PlaylistName}'.", playlistItem.Name);
        var inputTextBox = new TextBox { Text = playlistItem.Name };
        var dialog = new ContentDialog
        {
            Title = $"Rename '{playlistItem.Name}'", Content = inputTextBox, PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot
        };

        inputTextBox.TextChanged += (s, args) =>
            dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(inputTextBox.Text) &&
                                            inputTextBox.Text.Trim() != playlistItem.Name;
        dialog.IsPrimaryButtonEnabled = false;

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            _logger.LogInformation("User confirmed rename of playlist '{OldName}' to '{NewName}'.", playlistItem.Name,
                inputTextBox.Text);
            var argsTuple = new Tuple<Guid, string, bool>(playlistItem.Id, inputTextBox.Text, playlistItem.IsSmart);
            await ViewModel.RenamePlaylistCommand.ExecuteAsync(argsTuple);
        }
        else
        {
            _logger.LogInformation("User cancelled rename of playlist '{PlaylistName}'.", playlistItem.Name);
        }
    }

    private async void DeletePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PlaylistViewModelItem playlistItem } ||
            ViewModel.IsAnyOperationInProgress) return;

        _logger.LogInformation("Showing 'Delete Playlist' confirmation for '{PlaylistName}'.", playlistItem.Name);
        var dialog = new ContentDialog
        {
            Title = "Delete Playlist",
            Content =
                $"Are you sure you want to delete the playlist '{playlistItem.Name}'? This action cannot be undone.",
            PrimaryButtonText = "Delete", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            _logger.LogInformation("User confirmed deletion of playlist '{PlaylistName}' (Id: {PlaylistId}).",
                playlistItem.Name, playlistItem.Id);
            await ViewModel.DeletePlaylistCommand.ExecuteAsync(new Tuple<Guid, bool>(playlistItem.Id, playlistItem.IsSmart));
        }
        else
        {
            _logger.LogInformation("User cancelled deletion of playlist '{PlaylistName}'.", playlistItem.Name);
        }
    }

    private async void ChangeCover_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PlaylistViewModelItem playlistItem } ||
            ViewModel.IsAnyOperationInProgress) return;

        _logger.LogInformation("User initiated cover image change for playlist '{PlaylistName}'.", playlistItem.Name);
        var newCoverImageUri = await PickCoverImageAsync();

        if (!string.IsNullOrWhiteSpace(newCoverImageUri))
        {
            _logger.LogInformation("User selected new cover image for playlist '{PlaylistName}'. Updating.",
                playlistItem.Name);
            var argsTuple = new Tuple<Guid, string, bool>(playlistItem.Id, newCoverImageUri, playlistItem.IsSmart);
            await ViewModel.UpdatePlaylistCoverCommand.ExecuteAsync(argsTuple);
        }
        else
        {
            _logger.LogInformation("User cancelled cover image selection.");
        }
    }

    private async Task<string?> PickCoverImageAsync()
    {
        _logger.LogDebug("Opening file picker for cover image.");
        var picker = new FileOpenPicker();
        var hwnd = WindowNative.GetWindowHandle(App.RootWindow);
        InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".png");

        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            _logger.LogInformation("User picked image file: {FilePath}", file.Path);
            return file.Path;
        }

        _logger.LogInformation("User did not pick an image file.");
        return null;
    }
}
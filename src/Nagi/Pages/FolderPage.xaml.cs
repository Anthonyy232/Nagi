using System;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.Navigation;
using Nagi.ViewModels;
using WinRT.Interop;

namespace Nagi.Pages;

/// <summary>
///     A page that displays a grid of music folders and allows adding, deleting, and rescanning them.
/// </summary>
public sealed partial class FolderPage : Page
{
    public FolderPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<FolderViewModel>();
        DataContext = ViewModel;
    }

    public FolderViewModel ViewModel { get; }

    /// <summary>
    ///     Loads the folders from the ViewModel when the page is loaded.
    /// </summary>
    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadFoldersCommand.ExecuteAsync(null);
    }

    /// <summary>
    ///     Handles clicks on a folder item, navigating to the song list for that folder.
    /// </summary>
    private void FoldersGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FolderViewModelItem clickedFolder)
        {
            var navParam = new FolderSongViewNavigationParameter
            {
                Title = clickedFolder.Name,
                FolderId = clickedFolder.Id
            };
            Frame.Navigate(typeof(FolderSongViewPage), navParam);
        }
    }

    /// <summary>
    ///     Opens a folder picker to allow the user to add a new music folder.
    /// </summary>
    private async void AddFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsAnyOperationInProgress) return;

        var folderPicker = new FolderPicker();
        var hwnd = WindowNative.GetWindowHandle(App.RootWindow);
        InitializeWithWindow.Initialize(folderPicker, hwnd);
        folderPicker.FileTypeFilter.Add("*");

        var folder = await folderPicker.PickSingleFolderAsync();
        if (folder != null) await ViewModel.AddFolderAndScanCommand.ExecuteAsync(folder.Path);
    }

    /// <summary>
    ///     Handles the click event for the "Delete" context menu item.
    /// </summary>
    private async void DeleteFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FolderViewModelItem folderItem })
            await ShowDeleteFolderConfirmationDialogAsync(folderItem);
    }

    /// <summary>
    ///     Displays a confirmation dialog before deleting a folder from the library.
    /// </summary>
    private async Task ShowDeleteFolderConfirmationDialogAsync(FolderViewModelItem folderItem)
    {
        if (ViewModel.IsAnyOperationInProgress) return;

        var dialog = new ContentDialog
        {
            Title = "Delete Folder",
            Content =
                $"Are you sure you want to remove the folder '{folderItem.Name}' from the library? This will not delete the files from your computer.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary) await ViewModel.DeleteFolderCommand.ExecuteAsync(folderItem.Id);
    }
}
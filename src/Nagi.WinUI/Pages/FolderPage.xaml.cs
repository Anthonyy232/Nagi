using System;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.WinUI.ViewModels;
using WinRT.Interop;

namespace Nagi.WinUI.Pages;

/// <summary>
///     A page that displays a grid of music folders and allows adding, deleting, and rescanning them.
/// </summary>
public sealed partial class FolderPage : Page {
    private readonly ILogger<FolderPage> _logger;

    public FolderPage() {
        InitializeComponent();

        ViewModel = App.Services!.GetRequiredService<FolderViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<FolderPage>>();
        DataContext = ViewModel;

        _logger.LogInformation("FolderPage initialized.");
    }

    public FolderViewModel ViewModel { get; }

    /// <summary>
    ///     Loads the folders from the ViewModel when the page is loaded.
    /// </summary>
    private async void Page_Loaded(object sender, RoutedEventArgs e) {
        _logger.LogInformation("FolderPage loaded. Loading folders...");
        await ViewModel.LoadFoldersCommand.ExecuteAsync(null);
        _logger.LogInformation("Finished loading folders.");
    }

    /// <summary>
    ///     Handles the page's navigated-from event.
    ///     This is the critical cleanup step that disposes the ViewModel to prevent memory leaks.
    /// </summary>
    protected override void OnNavigatedFrom(NavigationEventArgs e) {
        base.OnNavigatedFrom(e);
        _logger.LogInformation("Navigating away from FolderPage. Disposing ViewModel.");
        ViewModel.Dispose();
    }

    /// <summary>
    ///     Handles clicks on a folder item, navigating to the song list for that folder.
    /// </summary>
    private void FoldersGridView_ItemClick(object sender, ItemClickEventArgs e) {
        if (e.ClickedItem is FolderViewModelItem clickedFolder) {
            _logger.LogInformation("User clicked on folder '{FolderName}' (Id: {FolderId}). Navigating to detail view.",
                clickedFolder.Name, clickedFolder.Id);
            ViewModel.NavigateToFolderDetail(clickedFolder);
        }
    }

    /// <summary>
    ///     Opens a folder picker to allow the user to add a new music folder.
    /// </summary>
    private async void AddFolderButton_Click(object sender, RoutedEventArgs e) {
        if (ViewModel.IsAnyOperationInProgress) {
            _logger.LogDebug("Add folder button clicked, but an operation is already in progress. Ignoring.");
            return;
        }

        _logger.LogInformation("Add folder button clicked. Opening folder picker.");
        var folderPicker = new FolderPicker();
        var hwnd = WindowNative.GetWindowHandle(App.RootWindow);
        InitializeWithWindow.Initialize(folderPicker, hwnd);
        folderPicker.FileTypeFilter.Add("*");

        var folder = await folderPicker.PickSingleFolderAsync();
        if (folder != null) {
            _logger.LogInformation("User selected folder '{FolderPath}'. Adding and scanning.", folder.Path);
            await ViewModel.AddFolderAndScanCommand.ExecuteAsync(folder.Path);
        }
        else {
            _logger.LogInformation("User cancelled the folder picker.");
        }
    }

    /// <summary>
    ///     Handles the click event for the "Delete" context menu item.
    /// </summary>
    private async void DeleteFolder_Click(object sender, RoutedEventArgs e) {
        if (sender is FrameworkElement { DataContext: FolderViewModelItem folderItem }) {
            _logger.LogInformation("Delete context menu clicked for folder '{FolderName}' (Id: {FolderId}).",
                folderItem.Name, folderItem.Id);
            await ShowDeleteFolderConfirmationDialogAsync(folderItem);
        }
    }

    /// <summary>
    ///     Displays a confirmation dialog before deleting a folder from the library.
    /// </summary>
    private async Task ShowDeleteFolderConfirmationDialogAsync(FolderViewModelItem folderItem) {
        if (ViewModel.IsAnyOperationInProgress) {
            _logger.LogDebug(
                "Attempted to show delete confirmation for '{FolderName}', but an operation is already in progress. Ignoring.",
                folderItem.Name);
            return;
        }

        _logger.LogInformation("Showing delete confirmation dialog for folder '{FolderName}'.", folderItem.Name);
        var dialog = new ContentDialog {
            Title = "Delete Folder",
            Content =
                $"Are you sure you want to remove the folder '{folderItem.Name}' from the library? This will not delete the files from your computer.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary) {
            _logger.LogInformation(
                "User confirmed deletion for folder '{FolderName}' (Id: {FolderId}). Executing delete command.",
                folderItem.Name, folderItem.Id);
            await ViewModel.DeleteFolderCommand.ExecuteAsync(folderItem.Id);
        }
        else {
            _logger.LogInformation("User cancelled deletion for folder '{FolderName}'.", folderItem.Name);
        }
    }
}
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.WinUI.Services.Abstractions;
using Nagi.WinUI.ViewModels;

namespace Nagi.WinUI.Pages;

/// <summary>
///     A page that displays a grid of music folders and allows adding, deleting, and rescanning them.
/// </summary>
public sealed partial class FolderPage : Page
{
    private readonly ILogger<FolderPage> _logger;
    private readonly IUIService _uiService;

    public FolderPage()
    {
        InitializeComponent();

        ViewModel = App.Services!.GetRequiredService<FolderViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<FolderPage>>();
        _uiService = App.Services!.GetRequiredService<IUIService>();
        DataContext = ViewModel;

        _logger.LogDebug("FolderPage initialized.");
    }

    public FolderViewModel ViewModel { get; }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _logger.LogDebug("FolderPage loaded. Loading folders...");
            await ViewModel.LoadFoldersCommand.ExecuteAsync(null);
            _logger.LogDebug("Finished loading folders.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during FolderPage initial loading");
        }
    }

    private void FoldersGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FolderViewModelItem clickedFolder)
        {
            _logger.LogDebug("User clicked on folder '{FolderName}' (Id: {FolderId}). Navigating to detail view.",
                clickedFolder.Name, clickedFolder.Id);
            ViewModel.NavigateToFolderDetail(clickedFolder);
        }
    }

    private async void AddFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsAnyOperationInProgress)
        {
            _logger.LogDebug("Add folder button clicked, but an operation is already in progress. Ignoring.");
            return;
        }

        try
        {
            _logger.LogDebug("Add folder button clicked. Opening folder picker.");
            var folderPath = await _uiService.PickSingleFolderAsync();
            if (folderPath != null)
            {
                _logger.LogDebug("User selected folder '{FolderPath}'. Adding and scanning.", folderPath);
                await ViewModel.AddFolderAndScanCommand.ExecuteAsync(folderPath);
            }
            else
            {
                _logger.LogDebug("User cancelled the folder picker.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding folder to library");
        }
    }

    private async void DeleteFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FolderViewModelItem folderItem })
        {
            try
            {
                _logger.LogDebug("Delete context menu clicked for folder '{FolderName}' (Id: {FolderId}).",
                    folderItem.Name, folderItem.Id);
                await ShowDeleteFolderConfirmationDialogAsync(folderItem);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initiating folder deletion for {FolderName}", folderItem.Name);
            }
        }
    }

    private async Task ShowDeleteFolderConfirmationDialogAsync(FolderViewModelItem folderItem)
    {
        if (ViewModel.IsAnyOperationInProgress)
        {
            _logger.LogDebug(
                "Attempted to show delete confirmation for '{FolderName}', but an operation is already in progress. Ignoring.",
                folderItem.Name);
            return;
        }

        _logger.LogDebug("Showing delete confirmation dialog for folder '{FolderName}'.", folderItem.Name);
        var confirmed = await _uiService.ShowConfirmationDialogAsync(
            Nagi.WinUI.Resources.Strings.FolderPage_DeleteDialog_Title,
            string.Format(Nagi.WinUI.Resources.Strings.FolderPage_DeleteDialog_Format, folderItem.Name),
            Nagi.WinUI.Resources.Strings.FolderPage_DeleteDialog_PrimaryButton,
            Nagi.WinUI.Resources.Strings.FolderPage_DeleteDialog_CloseButton);
        if (confirmed)
        {
            _logger.LogDebug(
                "User confirmed deletion for folder '{FolderName}' (Id: {FolderId}). Executing delete command.",
                folderItem.Name, folderItem.Id);
            await ViewModel.DeleteFolderCommand.ExecuteAsync(folderItem.Id);
        }
        else
        {
            _logger.LogDebug("User cancelled deletion for folder '{FolderName}'.", folderItem.Name);
        }
    }
}

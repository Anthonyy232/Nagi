using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.WinUI.ViewModels;

namespace Nagi.WinUI.Pages;

public sealed partial class FolderPage : Page
{
    private readonly ILogger<FolderPage> _logger;

    public FolderPage()
    {
        InitializeComponent();

        ViewModel = App.Services!.GetRequiredService<FolderViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<FolderPage>>();
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
        try
        {
            await ViewModel.AddFolderCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding folder to library");
        }
    }

    private async void DeleteFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FolderViewModelItem folderItem }) return;

        try
        {
            await ViewModel.DeleteFolderCommand.ExecuteAsync(folderItem.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting folder {FolderName}", folderItem.Name);
        }
    }
}

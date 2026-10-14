using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Presentation.ViewModels;

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
    }

    public FolderViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        try
        {
            base.OnNavigatedTo(e);
            await ViewModel.LoadFoldersCommand.ExecuteAsync(null);
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

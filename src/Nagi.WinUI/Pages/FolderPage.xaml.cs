using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Presentation.ViewModels;

namespace Nagi.WinUI.Pages;

public sealed partial class FolderPage : Page
{
    public FolderPage()
    {
        InitializeComponent();

        ViewModel = App.Services!.GetRequiredService<FolderViewModel>();
        DataContext = ViewModel;
    }

    public FolderViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadFoldersCommand.ExecuteAsync(null);
    }

    private void FoldersGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FolderViewModelItem clickedFolder)
        {
            ViewModel.NavigateToFolderDetail(clickedFolder);
        }
    }
}

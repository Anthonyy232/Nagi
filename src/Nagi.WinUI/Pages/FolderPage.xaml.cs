using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Presentation.Lists;

namespace Nagi.WinUI.Pages;

public sealed partial class FolderPage : ViewModelPage
{
    public FolderPage()
    {
        InitializeComponent();

        ViewModel = GetViewModel<FolderViewModel>();
        DataContext = ViewModel;
    }

    public FolderViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync(CancellationToken.None);
    }

    private void FoldersGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FolderViewModelItem clickedFolder) ViewModel.OpenFolder(clickedFolder);
    }
}

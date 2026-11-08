using Nagi.Presentation.Lists;

namespace Nagi.WinUI.Pages;

public sealed partial class FolderPage : CollectionPage
{
    public FolderPage()
    {
        InitializeComponent();
        ViewModel = GetViewModel<FolderViewModel>();
        DataContext = ViewModel;
        InitializeCollection(ViewModel, CollectionGrid);
    }

    public FolderViewModel ViewModel { get; }
}

using Nagi.Presentation.Lists;

namespace Nagi.WinUI.Pages;

public sealed partial class FolderPage : CollectionPage
{
    public FolderPage()
    {
        InitializeComponent();
        ViewModel = GetViewModel<FolderViewModel>();
        InitializeCollection(ViewModel, CollectionGrid);
    }

    public FolderViewModel ViewModel { get; }
}

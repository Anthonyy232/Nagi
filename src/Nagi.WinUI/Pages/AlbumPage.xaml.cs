using Nagi.Presentation.Lists;

namespace Nagi.WinUI.Pages;

public sealed partial class AlbumPage : CollectionPage
{
    public AlbumPage()
    {
        InitializeComponent();
        ViewModel = GetViewModel<AlbumViewModel>();
        DataContext = ViewModel;
        InitializeCollection(ViewModel, CollectionGrid);
    }

    public AlbumViewModel ViewModel { get; }
}

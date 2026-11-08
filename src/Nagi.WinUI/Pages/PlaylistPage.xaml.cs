using Nagi.Presentation.Lists;

namespace Nagi.WinUI.Pages;

public sealed partial class PlaylistPage : CollectionPage
{
    public PlaylistPage()
    {
        InitializeComponent();
        ViewModel = GetViewModel<PlaylistViewModel>();
        DataContext = ViewModel;
        InitializeCollection(ViewModel, CollectionGrid);
    }

    public PlaylistViewModel ViewModel { get; }
}

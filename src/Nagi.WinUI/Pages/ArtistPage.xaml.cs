using Nagi.Presentation.Lists;

namespace Nagi.WinUI.Pages;

public sealed partial class ArtistPage : CollectionPage
{
    public ArtistPage()
    {
        InitializeComponent();
        ViewModel = GetViewModel<ArtistViewModel>();
        InitializeCollection(ViewModel, CollectionGrid);
    }

    public ArtistViewModel ViewModel { get; }
}

using Nagi.Presentation.Lists;

namespace Nagi.WinUI.Pages;

public sealed partial class GenrePage : CollectionPage
{
    public GenrePage()
    {
        InitializeComponent();
        ViewModel = GetViewModel<GenreViewModel>();
        InitializeCollection(ViewModel, CollectionGrid);
    }

    public GenreViewModel ViewModel { get; }
}

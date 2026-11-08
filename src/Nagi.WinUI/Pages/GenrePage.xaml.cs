using Nagi.Presentation.Lists;

namespace Nagi.WinUI.Pages;

public sealed partial class GenrePage : CollectionPage
{
    public GenrePage()
    {
        InitializeComponent();
        ViewModel = GetViewModel<GenreViewModel>();
        DataContext = ViewModel;
        InitializeCollection(ViewModel, CollectionGrid);
    }

    public GenreViewModel ViewModel { get; }
}

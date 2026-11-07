using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Core.Library;
using Nagi.Presentation.Lists;

namespace Nagi.WinUI.Pages;

public sealed partial class GenrePage : Page
{
    public GenrePage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<GenreViewModel>();
        DataContext = ViewModel;
    }

    public GenreViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync(CancellationToken.None);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.ResetState();
    }

    private void GenresGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is GenreSummary clickedGenre) ViewModel.OpenGenre(clickedGenre);
    }
}

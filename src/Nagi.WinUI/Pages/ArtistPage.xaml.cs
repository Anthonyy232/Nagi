using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Presentation.ViewModels;

namespace Nagi.WinUI.Pages;

public sealed partial class ArtistPage : Page
{
    public ArtistPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<ArtistViewModel>();
        DataContext = ViewModel;
    }

    public ArtistViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync(CancellationToken.None);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // The singleton view model outlives the page, so it is reset rather than disposed.
        ViewModel.ResetState();
    }

    private void ArtistsGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ArtistViewModelItem clickedArtist) ViewModel.OpenArtist(clickedArtist);
    }
}

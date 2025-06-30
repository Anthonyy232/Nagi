using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.Navigation;
using Nagi.ViewModels;

namespace Nagi.Pages;

/// <summary>
///     A page that displays a grid of all artists in the library.
/// </summary>
public sealed partial class ArtistPage : Page
{
    public ArtistPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<ArtistViewModel>();
        Unloaded += Page_Unloaded;
    }

    public ArtistViewModel ViewModel { get; }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadArtistsCommand.ExecuteAsync(null);
    }

    private void ArtistsGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ArtistViewModelItem clickedArtist)
        {
            var navParam = new ArtistViewNavigationParameter
            {
                ArtistId = clickedArtist.Id,
                ArtistName = clickedArtist.Name
            };
            Frame.Navigate(typeof(ArtistViewPage), navParam);
        }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Cleanup();
    }
}
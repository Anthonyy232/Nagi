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
    }

    public ArtistViewModel ViewModel { get; }

    /// <summary>
    ///     Initiates artist loading when the page is loaded.
    /// </summary>
    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadArtistsCommand.ExecuteAsync(null);
    }

    /// <summary>
    ///     Navigates to the artist detail view when an artist item is clicked.
    /// </summary>
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
}
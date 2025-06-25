// Nagi/Pages/AlbumPage.xaml.cs

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Navigation;
using Nagi.ViewModels;

namespace Nagi.Pages;

/// <summary>
///     A page that displays a grid of all albums in the user's library.
/// </summary>
public sealed partial class AlbumPage : Page
{
    public AlbumPage()
    {
        InitializeComponent();
        // Resolve the ViewModel from the dependency injection container.
        ViewModel = App.Services.GetRequiredService<AlbumViewModel>();
    }

    public AlbumViewModel ViewModel { get; }

    /// <summary>
    ///     Invoked when the page is navigated to. Initiates loading of the album list.
    /// </summary>
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAlbumsAsync();
    }

    /// <summary>
    ///     Handles clicks on album items, navigating to the detailed view for the selected album.
    /// </summary>
    private void AlbumsGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AlbumViewModelItem clickedAlbum)
        {
            var navParam = new AlbumViewNavigationParameter
            {
                AlbumId = clickedAlbum.Id,
                AlbumTitle = clickedAlbum.Title,
                ArtistName = clickedAlbum.ArtistName
            };
            // Use the Frame to navigate to the AlbumViewPage, passing album details.
            Frame.Navigate(typeof(AlbumViewPage), navParam);
        }
    }
}
using System.Threading;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Presentation.Lists;

namespace Nagi.WinUI.Pages;

/// <summary>
///     A page with a grid of albums, artists, genres, playlists or folders, which loads when shown and opens the
///     item that is clicked.
/// </summary>
public abstract class CollectionPage : ViewModelPage
{
    private ICollectionViewModel? _collection;

    /// <summary>Call after InitializeComponent, with the grid styled <c>CollectionGridViewStyle</c>.</summary>
    protected void InitializeCollection(ICollectionViewModel collection, ListViewBase grid)
    {
        _collection = collection;
        grid.ItemClick += (_, e) => collection.Open(e.ClickedItem);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await _collection!.LoadAsync(CancellationToken.None);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _collection!.ResetState();
    }
}

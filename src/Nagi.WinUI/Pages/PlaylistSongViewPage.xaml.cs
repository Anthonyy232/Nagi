using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Nagi.Core.Library;
using Nagi.Presentation.Lists;
using Nagi.Presentation;
using Windows.ApplicationModel.DataTransfer;

namespace Nagi.WinUI.Pages;

public sealed partial class PlaylistSongViewPage : SongListPage
{

    public PlaylistSongViewPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<ViewModelFactory>().Create<PlaylistSongListViewModel>();
        DataContext = ViewModel;
        InitializeSongList(ViewModel, ColumnHeaderHost, SongsListView);
    }

    public PlaylistSongListViewModel ViewModel { get; }


    private void SongsListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        // A reorder raises Remove and Add rather than Move, then DragItemsCompleted once with the moved items.
        if (args.DropResult != DataPackageOperation.Move) return;
        _ = ViewModel.OnSongsReorderedAsync(args.Items.OfType<Song>().ToList());
    }
}

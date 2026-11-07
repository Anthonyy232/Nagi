using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Presentation.ViewModels;
using Nagi.Presentation.Navigation;

namespace Nagi.WinUI.Pages;

public sealed partial class LibraryPage : SongListPage
{
    public LibraryPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<LibraryViewModel>();
        DataContext = ViewModel;
        InitializeSongList(ViewModel, ColumnHeaderHost, SongsListView);
    }

    public LibraryViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ClearSelection();
        await ViewModel.OpenAsync(e.Parameter is NavigationTags.Favorites);
    }
}

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Presentation.Resources;
using Nagi.Presentation.ViewModels;

namespace Nagi.WinUI.Pages;

public sealed partial class LibraryPage : SongListPage
{
    public LibraryPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<LibraryViewModel>();
        DataContext = ViewModel;
        // The library view model is shared and the page is cached, so it outlives each visit.
        InitializeSongList(ViewModel, ownsViewModel: false, SearchToggleButton, SearchTextBox,
            Strings.LibraryPage_SearchButton_Search_ToolTip, ColumnHeaderHost, SongsListView);
    }

    public LibraryViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ClearSelection();
        await ViewModel.OpenAsync(e.Parameter is "Favorites");
    }
}

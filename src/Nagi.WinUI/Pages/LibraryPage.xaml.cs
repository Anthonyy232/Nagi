using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Presentation.Resources;
using Nagi.Presentation.ViewModels;

namespace Nagi.WinUI.Pages;

public sealed partial class LibraryPage : SongListPage
{
    private readonly ILogger<LibraryPage> _logger;

    public LibraryPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<LibraryViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<LibraryPage>>();
        DataContext = ViewModel;
        // The library view model is shared and the page is cached, so it outlives each visit.
        InitializeSongList(ViewModel, ownsViewModel: false, SearchToggleButton, SearchTextBox,
            Strings.LibraryPage_SearchButton_Search_ToolTip, SongsListView);
    }

    public LibraryViewModel ViewModel { get; }

    private string GetEmptyStateTitle(bool isSearchActive, bool favorites) => isSearchActive
        ? Strings.LibraryPage_SearchEmpty_Title
        : favorites ? Strings.Listening_FavoritesEmpty : Strings.LibraryPage_EmptyState_Title;

    private string GetEmptyStateSubtitle(bool isSearchActive, bool favorites) => isSearchActive
        ? Strings.LibraryPage_SearchEmpty_Subtitle
        : favorites ? Strings.Listening_FavoritesEmptyDescription : Strings.LibraryPage_EmptyState_Subtitle;

    private void OnArtistColumnSizeChanged(object sender, SizeChangedEventArgs e) =>
        ((FrameworkElement)((Grid)sender).Children[0]).MaxWidth = e.NewSize.Width;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ClearSelection();

        try
        {
            await ViewModel.SetLibraryViewAsync(e.Parameter is "Favorites");
            if (!ViewModel.HasLoadedPlaylists)
            {
                await ViewModel.LoadAvailablePlaylistsAsync();
            }

            if (!ViewModel.HasInitialized)
            {
                // Loads songs and triggers the initial background rescan (once per session).
                await ViewModel.InitializeAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize LibraryPage.");
        }
    }
}

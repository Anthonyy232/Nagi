using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.ViewModels;

namespace Nagi.WinUI.Pages;

public sealed partial class SmartPlaylistSongViewPage : SongListPage
{
    private readonly ILogger<SmartPlaylistSongViewPage> _logger;

    public SmartPlaylistSongViewPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<IViewModelFactory>().Create<SmartPlaylistSongListViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<SmartPlaylistSongViewPage>>();
        DataContext = ViewModel;
        InitializeSongList(ViewModel, ownsViewModel: true, SearchToggleButton, SearchTextBox,
            Strings.SmartPlaylistSongViewPage_SearchButton_Search_ToolTip, SongsListView);
    }

    public SmartPlaylistSongListViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is SmartPlaylistSongViewNavigationParameter navParam)
        {
            await ViewModel.InitializeAsync(navParam.Title, navParam.SmartPlaylistId, navParam.CoverImageUri);
        }
        else
        {
            var paramType = e.Parameter?.GetType().Name ?? "null";
            _logger.LogWarning(
                "Received invalid navigation parameter. Expected '{ExpectedType}', got '{ActualType}'. Initializing with fallback state.",
                nameof(SmartPlaylistSongViewNavigationParameter), paramType);
            await ViewModel.InitializeAsync(Strings.SmartPlaylistSongViewPage_UnknownPlaylist, null);
        }
    }
}

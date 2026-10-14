using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.ViewModels;

namespace Nagi.WinUI.Pages;

public sealed partial class GenreViewPage : SongListPage
{
    private readonly ILogger<GenreViewPage> _logger;

    public GenreViewPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<IViewModelFactory>().Create<GenreViewViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<GenreViewPage>>();
        DataContext = ViewModel;
        InitializeSongList(ViewModel, ownsViewModel: true, SearchToggleButton, SearchTextBox,
            Strings.GenreViewPage_SearchButton_Search_ToolTip, SongsListView);
    }

    public GenreViewViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is GenreViewNavigationParameter navParam)
        {
            try
            {
                await ViewModel.LoadGenreDetailsAsync(navParam);
                await ViewModel.LoadAvailablePlaylistsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load details for genre '{GenreName}'.", navParam.GenreName);
            }
        }
        else
        {
            var paramType = e.Parameter?.GetType().Name ?? "null";
            _logger.LogError(
                "Received incorrect navigation parameter type. Expected '{ExpectedType}', but got '{ActualType}'.",
                nameof(GenreViewNavigationParameter), paramType);
        }
    }
}

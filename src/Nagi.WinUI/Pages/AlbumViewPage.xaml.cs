using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.ViewModels;

namespace Nagi.WinUI.Pages;

public class DiscGroupTemplateSelector : DataTemplateSelector
{
    public DataTemplate? HeaderTemplate { get; set; }
    public DataTemplate? SongTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
    {
        return item is DiscHeader ? HeaderTemplate : SongTemplate;
    }
}

public class DiscGroupStyleSelector : StyleSelector
{
    public Style? HeaderStyle { get; set; }
    public Style? SongStyle { get; set; }

    protected override Style? SelectStyleCore(object item, DependencyObject container)
    {
        return item is DiscHeader ? HeaderStyle : SongStyle;
    }
}

public sealed partial class AlbumViewPage : SongListPage
{
    private readonly ILogger<AlbumViewPage> _logger;

    public AlbumViewPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<IViewModelFactory>().Create<AlbumViewViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<AlbumViewPage>>();
        DataContext = ViewModel;
        InitializeSongList(ViewModel, ownsViewModel: true, SearchToggleButton, SearchTextBox,
            Strings.AlbumViewPage_SearchButton_Search_ToolTip, SongsListView, GroupedSongsListView);
    }

    public AlbumViewViewModel ViewModel { get; }

    // Multi-disc albums show the songs in a list grouped under disc headers.
    protected override ListView ActiveSongList => ViewModel.IsGroupedByDisc ? GroupedSongsListView : SongsListView;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is AlbumViewNavigationParameter navParam)
        {
            try
            {
                await ViewModel.LoadAlbumDetailsAsync(navParam.AlbumId);
                await ViewModel.LoadAvailablePlaylistsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load details for AlbumId: {AlbumId}", navParam.AlbumId);
            }
        }
        else
        {
            var paramType = e.Parameter?.GetType().Name ?? "null";
            _logger.LogError(
                "Received incorrect navigation parameter type. Expected '{ExpectedType}', but got '{ActualType}'.",
                nameof(AlbumViewNavigationParameter), paramType);
        }
    }
}

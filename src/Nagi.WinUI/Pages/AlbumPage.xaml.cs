using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Presentation.Resources;
using Nagi.Presentation.ViewModels;

namespace Nagi.WinUI.Pages;

public sealed partial class AlbumPage : SearchablePage
{
    private readonly ILogger<AlbumPage> _logger;
    private CancellationTokenSource? _cancellationTokenSource;

    public AlbumPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<AlbumViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<AlbumPage>>();
        DataContext = ViewModel;
        InitializeSearch(ViewModel, SearchToggleButton, SearchTextBox, Strings.AlbumPage_SearchButton_Search_ToolTip);

        _logger.LogDebug("AlbumPage initialized.");
    }

    public AlbumViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        try
        {
            base.OnNavigatedTo(e);
            _logger.LogDebug("Navigated to AlbumPage.");
            var cts = new CancellationTokenSource();
            _cancellationTokenSource = cts;

            await ViewModel.LoadAsync(cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to navigate to AlbumPage correctly.");
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _logger.LogDebug("Navigating away from AlbumPage.");

        ViewModel.ResetState();
        _cancellationTokenSource?.Cancel();

        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
        // Note: ViewModel is Singleton, do not dispose - state persists across navigations
    }

    private void AlbumsGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AlbumViewModelItem clickedAlbum)
        {
            _logger.LogDebug(
                "User clicked on album '{AlbumTitle}' by '{ArtistName}' (Id: {AlbumId}). Navigating to detail view.",
                clickedAlbum.Title, clickedAlbum.ArtistName, clickedAlbum.Id);
            _ = ViewModel.NavigateToAlbumDetailAsync(clickedAlbum);
        }
    }
}

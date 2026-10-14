using System;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Presentation.Resources;
using Nagi.Presentation.ViewModels;

namespace Nagi.WinUI.Pages;

public sealed partial class ArtistPage : SearchablePage
{
    private readonly ILogger<ArtistPage> _logger;
    private CancellationTokenSource? _cancellationTokenSource;

    public ArtistPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<ArtistViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<ArtistPage>>();
        DataContext = ViewModel;
        InitializeSearch(ViewModel, SearchToggleButton, SearchTextBox, Strings.ArtistPage_SearchButton_Search_ToolTip);

        _logger.LogDebug("ArtistPage initialized.");
    }

    public ArtistViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        try
        {
            base.OnNavigatedTo(e);
            _logger.LogDebug("Navigated to ArtistPage.");
            var cts = new CancellationTokenSource();
            _cancellationTokenSource = cts;
            ViewModel.SubscribeToEvents();

            await ViewModel.LoadAsync(cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to navigate to ArtistPage correctly.");
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _logger.LogDebug("Navigating away from ArtistPage.");
        ViewModel.UnsubscribeFromEvents();

        ViewModel.ResetState();
        _cancellationTokenSource?.Cancel();

        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
        // Note: ViewModel is Singleton, do not dispose - state persists across navigations
    }

    private void ArtistsGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ArtistViewModelItem clickedArtist)
        {
            _logger.LogDebug(
                "User clicked on artist '{ArtistName}' (Id: {ArtistId}). Navigating to detail view.",
                clickedArtist.Name, clickedArtist.Id);
            _ = ViewModel.NavigateToArtistDetailAsync(clickedArtist);
        }
    }

    private async void ChangeImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ArtistViewModelItem artistItem }) return;

        try
        {
            _logger.LogDebug("User initiated image change for artist '{ArtistName}'.", artistItem.Name);
            await ViewModel.ChangeArtistImageCommand.ExecuteAsync(artistItem.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error changing image for artist {ArtistName}", artistItem.Name);
        }
    }

    private async void RemoveImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ArtistViewModelItem artistItem }) return;

        try
        {
            _logger.LogDebug("User requested removal of custom image for artist '{ArtistName}'.", artistItem.Name);
            await ViewModel.RemoveArtistImageCommand.ExecuteAsync(artistItem.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing image for artist {ArtistName}", artistItem.Name);
        }
    }

}

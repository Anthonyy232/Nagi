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

    public ArtistPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<ArtistViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<ArtistPage>>();
        DataContext = ViewModel;
        InitializeSearch(ViewModel, SearchToggleButton, SearchTextBox, Strings.ArtistPage_SearchButton_Search_ToolTip);
    }

    public ArtistViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        try
        {
            base.OnNavigatedTo(e);
            ViewModel.SubscribeToEvents();

            await ViewModel.LoadAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to navigate to ArtistPage correctly.");
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.UnsubscribeFromEvents();

        ViewModel.ResetState();
        // Note: ViewModel is Singleton, do not dispose - state persists across navigations
    }

    private void ArtistsGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ArtistViewModelItem clickedArtist)
        {
            _ = ViewModel.NavigateToArtistDetailAsync(clickedArtist);
        }
    }

    private async void ChangeImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ArtistViewModelItem artistItem }) return;

        try
        {
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
            await ViewModel.RemoveArtistImageCommand.ExecuteAsync(artistItem.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing image for artist {ArtistName}", artistItem.Name);
        }
    }

}

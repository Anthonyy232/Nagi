using System;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Presentation.Resources;
using Nagi.Presentation.ViewModels;

namespace Nagi.WinUI.Pages;

public sealed partial class ArtistPage : SearchablePage
{
    public ArtistPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<ArtistViewModel>();
        DataContext = ViewModel;
        InitializeSearch(ViewModel, SearchToggleButton, SearchTextBox, Strings.ArtistPage_SearchButton_Search_ToolTip);
    }

    public ArtistViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync(CancellationToken.None);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
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
}

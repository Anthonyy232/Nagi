using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.ViewModels;

namespace Nagi.WinUI.Pages;

public sealed partial class InsightsPage : Page
{
    public InsightsPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<InsightsViewModel>();
        DataContext = ViewModel;
    }

    public InsightsViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadInsightsAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.CloseSeeAllCommand.Execute(null);
    }

    private void OnSongsSortByPlayCountClick(object sender, RoutedEventArgs e) =>
        ViewModel.SongsSortMetric = SortMetric.PlayCount;

    private void OnSongsSortByDurationClick(object sender, RoutedEventArgs e) =>
        ViewModel.SongsSortMetric = SortMetric.Duration;

    private void OnArtistsSortByPlayCountClick(object sender, RoutedEventArgs e) =>
        ViewModel.ArtistsSortMetric = SortMetric.PlayCount;

    private void OnArtistsSortByDurationClick(object sender, RoutedEventArgs e) =>
        ViewModel.ArtistsSortMetric = SortMetric.Duration;

    private void OnAlbumsSortByPlayCountClick(object sender, RoutedEventArgs e) =>
        ViewModel.AlbumsSortMetric = SortMetric.PlayCount;

    private void OnAlbumsSortByDurationClick(object sender, RoutedEventArgs e) =>
        ViewModel.AlbumsSortMetric = SortMetric.Duration;

    private void OnGenresSortByPlayCountClick(object sender, RoutedEventArgs e) =>
        ViewModel.GenresSortMetric = SortMetric.PlayCount;

    private void OnGenresSortByDurationClick(object sender, RoutedEventArgs e) =>
        ViewModel.GenresSortMetric = SortMetric.Duration;
}

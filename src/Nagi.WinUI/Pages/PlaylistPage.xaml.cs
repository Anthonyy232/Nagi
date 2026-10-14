using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Presentation.Resources;
using Nagi.Presentation.ViewModels;

namespace Nagi.WinUI.Pages;

public sealed partial class PlaylistPage : SearchablePage
{
    private readonly ILogger<PlaylistPage> _logger;

    public PlaylistPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<PlaylistViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<PlaylistPage>>();
        DataContext = ViewModel;
        InitializeSearch(ViewModel, SearchToggleButton, SearchTextBox, Strings.PlaylistPage_SearchButton_Search_ToolTip);
    }

    public PlaylistViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        try
        {
            base.OnNavigatedTo(e);
            await ViewModel.LoadPlaylistsCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during PlaylistPage initial loading");
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.ResetState();
    }

    private static PlaylistViewModelItem? ItemOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as PlaylistViewModelItem;

    private async void PlayPlaylist_Click(object sender, RoutedEventArgs e) =>
        await RunCommandAsync(ViewModel.PlayPlaylistCommand, ItemOf(sender), "playing playlist");

    private void PlaylistsGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PlaylistViewModelItem clickedPlaylist)
        {
            ViewModel.NavigateToPlaylistDetail(clickedPlaylist);
        }
    }

    private async void CreateNewPlaylistButton_Click(object sender, RoutedEventArgs e) =>
        await RunCommandAsync(ViewModel.CreatePlaylistCommand, null, "creating playlist");

    private async void CreateSmartPlaylistButton_Click(object sender, RoutedEventArgs e) =>
        await RunCommandAsync(ViewModel.CreateSmartPlaylistCommand, null, "creating smart playlist");

    private async void RenamePlaylist_Click(object sender, RoutedEventArgs e) =>
        await RunCommandAsync(ViewModel.RenamePlaylistCommand, ItemOf(sender), "renaming playlist");

    private async void DeletePlaylist_Click(object sender, RoutedEventArgs e) =>
        await RunCommandAsync(ViewModel.DeletePlaylistCommand, ItemOf(sender), "deleting playlist");

    private async void ChangeCover_Click(object sender, RoutedEventArgs e) =>
        await RunCommandAsync(ViewModel.ChangePlaylistCoverCommand, ItemOf(sender), "changing playlist cover");

    private async void RemoveImage_Click(object sender, RoutedEventArgs e) =>
        await RunCommandAsync(ViewModel.RemovePlaylistCoverCommand, ItemOf(sender), "removing playlist cover");

    private async Task RunCommandAsync(IAsyncRelayCommand command, object? parameter, string action)
    {
        try
        {
            await command.ExecuteAsync(parameter);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error {Action}", action);
        }
    }
}

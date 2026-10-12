using System;
using System.Threading.Tasks;
using Windows.System;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Nagi.WinUI.ViewModels;

namespace Nagi.WinUI.Pages;

public sealed partial class PlaylistPage : Page
{
    private readonly ILogger<PlaylistPage> _logger;
    private bool _isSearchExpanded;

    public PlaylistPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<PlaylistViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<PlaylistPage>>();
        DataContext = ViewModel;
        _logger.LogDebug("PlaylistPage initialized.");
    }

    public PlaylistViewModel ViewModel { get; }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _logger.LogDebug("PlaylistPage loaded. Setting initial visual state and loading playlists...");
            _isSearchExpanded = !string.IsNullOrWhiteSpace(ViewModel.SearchTerm);
            ToolTipService.SetToolTip(SearchToggleButton, _isSearchExpanded
                ? Nagi.WinUI.Resources.Strings.PlaylistPage_SearchButton_Close_ToolTip
                : Nagi.WinUI.Resources.Strings.PlaylistPage_SearchButton_Search_ToolTip);
            VisualStateManager.GoToState(this, _isSearchExpanded ? "SearchExpanded" : "SearchCollapsed", false);
            await ViewModel.LoadPlaylistsCommand.ExecuteAsync(null);
            _logger.LogDebug("Finished loading playlists.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during PlaylistPage initial loading");
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _logger.LogDebug("Navigating away from PlaylistPage.");
        ViewModel.ResetState();
    }

    private void OnSearchToggleButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isSearchExpanded)
            CollapseSearch();
        else
            ExpandSearch();
    }

    private void OnSearchTextBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            _logger.LogDebug("Escape key pressed in search box. Collapsing search.");
            CollapseSearch();
            e.Handled = true;
        }
    }

    private void ExpandSearch()
    {
        if (_isSearchExpanded) return;

        _isSearchExpanded = true;
        _logger.LogDebug("Search UI expanded.");
        ToolTipService.SetToolTip(SearchToggleButton, Nagi.WinUI.Resources.Strings.PlaylistPage_SearchButton_Close_ToolTip);
        VisualStateManager.GoToState(this, "SearchExpanded", false);
        SearchTextBox.Focus(FocusState.Programmatic);
    }

    private void CollapseSearch()
    {
        if (!_isSearchExpanded) return;

        _isSearchExpanded = false;
        _logger.LogDebug("Search UI collapsed and search term cleared.");
        ToolTipService.SetToolTip(SearchToggleButton, Nagi.WinUI.Resources.Strings.PlaylistPage_SearchButton_Search_ToolTip);
        VisualStateManager.GoToState(this, "SearchCollapsed", false);
        SearchToggleButton.Focus(FocusState.Programmatic);
        ViewModel.SearchTerm = string.Empty;
    }
    private static PlaylistViewModelItem? ItemOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as PlaylistViewModelItem;

    private async void PlayPlaylist_Click(object sender, RoutedEventArgs e) =>
        await RunCommandAsync(ViewModel.PlayPlaylistCommand, ItemOf(sender), "playing playlist");

    private void PlaylistsGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PlaylistViewModelItem clickedPlaylist)
        {
            _logger.LogDebug(
                "User clicked on playlist '{PlaylistName}' (Id: {PlaylistId}). Navigating to detail view.",
                clickedPlaylist.Name, clickedPlaylist.Id);
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

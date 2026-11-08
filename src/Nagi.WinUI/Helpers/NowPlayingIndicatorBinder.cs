using System.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Nagi.Presentation.Player;
using Nagi.WinUI.Controls;

namespace Nagi.WinUI.Helpers;

/// <summary>
///     Keeps the realized rows of a list, whose templates implement <see cref="INowPlayingRow" />, showing which song
///     the player has.
/// </summary>
public sealed class NowPlayingIndicatorBinder : IDisposable
{
    private readonly ListView _listView;
    private readonly PlayerViewModel _player;
    private readonly Func<object?, Guid?> _songIdExtractor;

    /// <param name="songIdExtractor">Maps a row item to the Id of the song it shows, or null for rows that aren't songs.</param>
    public NowPlayingIndicatorBinder(ListView listView, PlayerViewModel player, Func<object?, Guid?> songIdExtractor)
    {
        _listView = listView;
        _player = player;
        _songIdExtractor = songIdExtractor;

        _listView.ContainerContentChanging += OnContainerContentChanging;
        _player.PropertyChanged += OnPlayerPropertyChanged;
        // A cached page coming back has rows already, for which ContainerContentChanging doesn't fire again.
        RefreshAllRealizedContainers();
    }

    public void Dispose()
    {
        _listView.ContainerContentChanging -= OnContainerContentChanging;
        _player.PropertyChanged -= OnPlayerPropertyChanged;
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        // A recycled container fires again once it holds its new item.
        if (args.InRecycleQueue) return;

        UpdateContainer(args.ItemContainer, args.Item);
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(PlayerViewModel.CurrentPlayingSongId) or nameof(PlayerViewModel.IsPlaying))) return;
        RefreshAllRealizedContainers();
    }

    private void RefreshAllRealizedContainers()
    {
        if (_listView.ItemsPanelRoot is null) return;
        foreach (var child in _listView.ItemsPanelRoot.Children)
        {
            if (child is not SelectorItem container) continue;
            UpdateContainer(container, container.Content);
        }
    }

    private void UpdateContainer(SelectorItem? container, object? item)
    {
        if (container?.ContentTemplateRoot is not INowPlayingRow row) return;
        var songId = _songIdExtractor(item);
        row.SetNowPlaying(songId is not null && songId == _player.CurrentPlayingSongId, _player.IsPlaying);
    }
}

using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Nagi.Core.Models;
using Nagi.WinUI.Controls;
using Nagi.WinUI.ViewModels;

namespace Nagi.WinUI.Helpers;

/// <summary>
///     Updates the named NowPlayingIndicator and SongTitle in realized song rows.
/// </summary>
public sealed class NowPlayingIndicatorBinder : IDisposable
{
    private const string IndicatorElementName = "NowPlayingIndicator";
    private const string TitleElementName = "SongTitle";

    private readonly ListView _listView;
    private readonly SongListViewModelBase _viewModel;
    private readonly PlayerViewModel _playerViewModel;
    private readonly Brush _playingTitleBrush;
    private readonly Func<object?, Guid?> _songIdExtractor;

    private bool _disposed;

    /// <param name="songIdExtractor">
    /// Maps a row item (whatever the ListView's ItemsSource yields) to the Song's Id.
    /// Defaults to <c>(item as Song)?.Id</c> for lists of bare <see cref="Song"/>;
    /// pages that wrap Song in a row view-model (e.g. FolderContentItem) should pass
    /// their own extractor like <c>item =&gt; (item as FolderContentItem)?.Song?.Id</c>.
    /// </param>
    public NowPlayingIndicatorBinder(
        ListView listView,
        SongListViewModelBase viewModel,
        PlayerViewModel playerViewModel,
        Brush playingTitleBrush,
        Func<object?, Guid?>? songIdExtractor = null)
    {
        _listView = listView ?? throw new ArgumentNullException(nameof(listView));
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _playerViewModel = playerViewModel ?? throw new ArgumentNullException(nameof(playerViewModel));
        _playingTitleBrush = playingTitleBrush ?? throw new ArgumentNullException(nameof(playingTitleBrush));
        _songIdExtractor = songIdExtractor ?? (item => (item as Song)?.Id);

        _listView.ContainerContentChanging += OnContainerContentChanging;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _playerViewModel.PropertyChanged += OnPlayerViewModelPropertyChanged;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _listView.ContainerContentChanging -= OnContainerContentChanging;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _playerViewModel.PropertyChanged -= OnPlayerViewModelPropertyChanged;
    }

    /// <summary>
    ///     Force the binder to walk every realized container and reapply state. Call
    ///     this immediately after attaching to a page that may already have realized
    ///     item containers (e.g. WinUI cached pages on re-navigation), since
    ///     ContainerContentChanging won't fire for containers that already exist.
    /// </summary>
    public void Refresh() => RefreshAllRealizedContainers();

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        // Recycled containers will be re-bound to a different item; ignore until the
        // framework re-fires with the new content.
        if (args.InRecycleQueue) return;

        UpdateContainer(args.ItemContainer, args.Item);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SongListViewModelBase.CurrentPlayingSongId)) return;
        RefreshAllRealizedContainers();
    }

    private void OnPlayerViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlayerViewModel.IsPlaying)) return;
        // Only the IsPlaying child property of the indicator needs to update; iterate
        // realized containers and toggle the animation play/pause state.
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
        if (container?.ContentTemplateRoot is not FrameworkElement root) return;

        var rowSongId = _songIdExtractor(item);
        var isThisRowPlaying = rowSongId is not null && _viewModel.CurrentPlayingSongId == rowSongId;

        if (root.FindName(IndicatorElementName) is NowPlayingIndicator indicator)
        {
            indicator.IsActive = isThisRowPlaying;
            indicator.IsPlaying = _playerViewModel.IsPlaying;
        }

        if (root.FindName(TitleElementName) is TextBlock title)
        {
            if (isThisRowPlaying)
            {
                title.Foreground = _playingTitleBrush;
            }
            else
            {
                title.ClearValue(TextBlock.ForegroundProperty);
            }
        }
    }
}

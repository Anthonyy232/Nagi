using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Nagi.Presentation.Player;
using Nagi.WinUI.Controls;

namespace Nagi.WinUI.Helpers;

/// <summary>
///     Updates the named NowPlayingIndicator and SongTitle in realized song rows.
/// </summary>
public sealed class NowPlayingIndicatorBinder : IDisposable
{
    private const string IndicatorElementName = "NowPlayingIndicator";
    private const string TitleElementName = "SongTitle";

    private readonly ListView _listView;
    private readonly INowPlayingSource _source;
    private readonly Brush _playingTitleBrush;
    private readonly Func<object?, Guid?> _songIdExtractor;

    /// <param name="songIdExtractor">Maps a row item to the Id of the song it shows, or null for rows that aren't songs.</param>
    public NowPlayingIndicatorBinder(ListView listView, INowPlayingSource source, Func<object?, Guid?> songIdExtractor)
    {
        _listView = listView;
        _source = source;
        _playingTitleBrush = (Brush)Application.Current.Resources["AppPrimaryColorBrush"];
        _songIdExtractor = songIdExtractor;

        _listView.ContainerContentChanging += OnContainerContentChanging;
        _source.PropertyChanged += OnSourcePropertyChanged;
        // A cached page coming back has rows already, for which ContainerContentChanging doesn't fire again.
        RefreshAllRealizedContainers();
    }

    public void Dispose()
    {
        _listView.ContainerContentChanging -= OnContainerContentChanging;
        _source.PropertyChanged -= OnSourcePropertyChanged;
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        // Recycled containers will be re-bound to a different item; ignore until the
        // framework re-fires with the new content.
        if (args.InRecycleQueue) return;

        UpdateContainer(args.ItemContainer, args.Item);
    }

    private void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(INowPlayingSource.CurrentPlayingSongId) or nameof(INowPlayingSource.IsPlaying))) return;
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
        if (container?.ContentTemplateRoot is not FrameworkElement templateRoot) return;
        // A row that is a user control, like SongRow, keeps its names in its own namescope.
        var root = templateRoot is UserControl { Content: FrameworkElement content } ? content : templateRoot;

        var rowSongId = _songIdExtractor(item);
        var isThisRowPlaying = rowSongId is not null && _source.CurrentPlayingSongId == rowSongId;

        if (root.FindName(IndicatorElementName) is NowPlayingIndicator indicator)
        {
            indicator.IsActive = isThisRowPlaying;
            indicator.IsPlaying = _source.IsPlaying;
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

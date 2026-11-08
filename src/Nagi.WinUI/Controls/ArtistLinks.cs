using System.Collections.Generic;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Nagi.Core.Library;
using Nagi.Presentation.Navigation;
using ArtistNames = Nagi.Presentation.Lists.ArtistLinks;
using Nagi.WinUI.Styles;

namespace Nagi.WinUI.Controls;

/// <summary>
///     The artists of a song or an album as links that open each artist. The links take the control's font size and
///     foreground, and the last one trims when the control runs out of room.
/// </summary>
public sealed partial class ArtistLinks : UserControl
{
    public static readonly DependencyProperty SongProperty = DependencyProperty.Register(
        nameof(Song), typeof(Song), typeof(ArtistLinks), new PropertyMetadata(null, OnLinksChanged));

    public static readonly DependencyProperty AlbumArtistsProperty = DependencyProperty.Register(
        nameof(AlbumArtists), typeof(ICollection<AlbumArtist>), typeof(ArtistLinks), new PropertyMetadata(null, OnLinksChanged));

    public static readonly DependencyProperty ArtistNameProperty = DependencyProperty.Register(
        nameof(ArtistName), typeof(string), typeof(ArtistLinks), new PropertyMetadata(null, OnLinksChanged));

    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(
        nameof(Command), typeof(ICommand), typeof(ArtistLinks), new PropertyMetadata(null, OnLinksChanged));

    private readonly Grid _links = new();

    public ArtistLinks()
    {
        IsTabStop = false;
        Content = _links;
        UpdateLinks();
    }

    /// <summary>The song whose credited artists are linked. Leave it null to link <see cref="AlbumArtists" />.</summary>
    public Song? Song
    {
        get => (Song?)GetValue(SongProperty);
        set => SetValue(SongProperty, value);
    }

    public ICollection<AlbumArtist>? AlbumArtists
    {
        get => (ICollection<AlbumArtist>?)GetValue(AlbumArtistsProperty);
        set => SetValue(AlbumArtistsProperty, value);
    }

    /// <summary>The stored artist name, shown as one link when the artists themselves aren't loaded.</summary>
    public string? ArtistName
    {
        get => (string?)GetValue(ArtistNameProperty);
        set => SetValue(ArtistNameProperty, value);
    }

    /// <summary>Opens an artist; its parameter is an <see cref="ArtistNavigationRequest" />.</summary>
    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    private static void OnLinksChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ArtistLinks)d).UpdateLinks();

    private void UpdateLinks()
    {
        _links.Children.Clear();
        _links.ColumnDefinitions.Clear();

        var names = ArtistNames.GetNames(Song, AlbumArtists, ArtistName);
        if (names.Count == 0)
        {
            AddCell(CreateTrimmedText(Artist.UnknownArtistName), isLast: true);
            return;
        }

        var showsStoredSongName = ArtistNames.ShowsStoredSongName(Song, AlbumArtists);
        for (var i = 0; i < names.Count; i++)
        {
            var isLast = i == names.Count - 1;
            var link = new HyperlinkButton
            {
                Style = (Style)Application.Current.Resources[AppResourceKeys.SongListHyperlinkButtonStyle],
                Content = CreateTrimmedText(names[i]),
                Command = Command,
                CommandParameter = new ArtistNavigationRequest { Song = Song, ArtistName = showsStoredSongName ? null : names[i] }
            };
            // The link style sets a size and color of its own, which these replace.
            link.SetBinding(FontSizeProperty, new Binding { Source = this, Path = new PropertyPath(nameof(FontSize)) });
            link.SetBinding(ForegroundProperty, new Binding { Source = this, Path = new PropertyPath(nameof(Foreground)) });
            AddCell(link, isLast);

            if (!isLast)
                AddCell(new TextBlock
                {
                    Text = Artist.ArtistSeparator.Trim(),
                    Margin = new Thickness(4, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center
                }, isLast: false);
        }
    }

    private static TextBlock CreateTrimmedText(string text) => new()
    {
        Text = text,
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextWrapping = TextWrapping.NoWrap,
        VerticalAlignment = VerticalAlignment.Center
    };

    // The last cell takes what room is left, so it is the one that trims.
    private void AddCell(FrameworkElement element, bool isLast)
    {
        _links.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = isLast ? new GridLength(1, GridUnitType.Star) : GridLength.Auto
        });
        Grid.SetColumn(element, _links.ColumnDefinitions.Count - 1);
        _links.Children.Add(element);
    }
}

using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Nagi.Core.Models;
using Nagi.Presentation.Helpers;
using Nagi.Presentation.Navigation;

namespace Nagi.WinUI.Helpers;

public static class MultiArtistHyperlinkHelper
{
    public static readonly DependencyProperty SongProperty =
        DependencyProperty.RegisterAttached(
            "Song",
            typeof(Song),
            typeof(MultiArtistHyperlinkHelper),
            new PropertyMetadata(null, OnSongOrArtistNameChanged));

    public static readonly DependencyProperty AlbumArtistsProperty =
        DependencyProperty.RegisterAttached(
            "AlbumArtists",
            typeof(ICollection<AlbumArtist>),
            typeof(MultiArtistHyperlinkHelper),
            new PropertyMetadata(null, OnSongOrArtistNameChanged));

    public static readonly DependencyProperty ArtistNameProperty =
        DependencyProperty.RegisterAttached(
            "ArtistName",
            typeof(string),
            typeof(MultiArtistHyperlinkHelper),
            new PropertyMetadata(null, OnSongOrArtistNameChanged));

    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.RegisterAttached(
            "Command",
            typeof(ICommand),
            typeof(MultiArtistHyperlinkHelper),
            new PropertyMetadata(null, OnSongOrArtistNameChanged));

    public static Song? GetSong(DependencyObject obj) => (Song?)obj.GetValue(SongProperty);
    public static void SetSong(DependencyObject obj, Song? value) => obj.SetValue(SongProperty, value);

    public static ICollection<AlbumArtist>? GetAlbumArtists(DependencyObject obj) => (ICollection<AlbumArtist>?)obj.GetValue(AlbumArtistsProperty);
    public static void SetAlbumArtists(DependencyObject obj, ICollection<AlbumArtist>? value) => obj.SetValue(AlbumArtistsProperty, value);

    public static string? GetArtistName(DependencyObject obj) => (string?)obj.GetValue(ArtistNameProperty);
    public static void SetArtistName(DependencyObject obj, string? value) => obj.SetValue(ArtistNameProperty, value);

    public static ICommand? GetCommand(DependencyObject obj) => (ICommand?)obj.GetValue(CommandProperty);
    public static void SetCommand(DependencyObject obj, ICommand? value) => obj.SetValue(CommandProperty, value);

    private static void OnSongOrArtistNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBlock textBlock)
        {
            if (textBlock.IsLoaded && textBlock.Parent != null)
            {
                UpdateHyperlinks(textBlock);
            }
            else
            {
                textBlock.Loaded -= TextBlock_Loaded;
                textBlock.Loaded += TextBlock_Loaded;
            }
        }
    }

    private static void TextBlock_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock textBlock)
        {
            textBlock.Loaded -= TextBlock_Loaded;
            UpdateHyperlinks(textBlock);
        }
    }

    private static readonly DependencyProperty AssociatedStackPanelProperty =
        DependencyProperty.RegisterAttached(
            "AssociatedStackPanel",
            typeof(StackPanel),
            typeof(MultiArtistHyperlinkHelper),
            new PropertyMetadata(null));

    private static void UpdateHyperlinks(TextBlock textBlock)
    {
        var song = GetSong(textBlock);
        var albumArtists = GetAlbumArtists(textBlock);
        var artistString = GetArtistName(textBlock);
        var command = GetCommand(textBlock);

        if (textBlock.Parent is not Panel parentPanel)
        {
            textBlock.Text = artistString ?? string.Empty;
            return;
        }
        var stackPanel = (StackPanel?)textBlock.GetValue(AssociatedStackPanelProperty);
        if (stackPanel == null)
        {
            stackPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 0,
                VerticalAlignment = textBlock.VerticalAlignment,
                HorizontalAlignment = textBlock.HorizontalAlignment
            };
            if (parentPanel is Grid)
            {
                Grid.SetColumn(stackPanel, Grid.GetColumn(textBlock));
                Grid.SetRow(stackPanel, Grid.GetRow(textBlock));
                Grid.SetColumnSpan(stackPanel, Grid.GetColumnSpan(textBlock));
                Grid.SetRowSpan(stackPanel, Grid.GetRowSpan(textBlock));
            }
            textBlock.Visibility = Visibility.Collapsed;

            parentPanel.Children.Insert(parentPanel.Children.IndexOf(textBlock) + 1, stackPanel);

            textBlock.SetValue(AssociatedStackPanelProperty, stackPanel);
        }
        stackPanel.Children.Clear();

        var artistParts = ArtistLinks.GetNames(song, albumArtists, artistString);
        var showsStoredSongName = ArtistLinks.ShowsStoredSongName(song, albumArtists);
        if (artistParts.Count == 0)
        {
            var unknownArtist = new TextBlock
            {
                Text = Artist.UnknownArtistName,
                Style = Application.Current.Resources["BodyTextBlockStyle"] as Style
            };
            unknownArtist.SetBinding(TextBlock.ForegroundProperty, new Binding
            {
                Source = textBlock, Path = new PropertyPath(nameof(TextBlock.Foreground)), Mode = BindingMode.OneWay
            });
            stackPanel.Children.Add(unknownArtist);
            return;
        }

        for (int i = 0; i < artistParts.Count; i++)
        {
            var artistPart = artistParts[i];

            var button = new HyperlinkButton
            {
                Content = artistPart,
                Command = command,
                CommandParameter = new ArtistNavigationRequest { Song = song, ArtistName = showsStoredSongName ? null : artistPart }
            };
            if (Application.Current.Resources.TryGetValue("SongListHyperlinkButtonStyle", out var styleObj) && styleObj is Style style)
            {
                button.Style = style;
            }

            button.SetBinding(Control.ForegroundProperty, new Binding
            {
                Source = textBlock, Path = new PropertyPath(nameof(TextBlock.Foreground)), Mode = BindingMode.OneWay
            });

            stackPanel.Children.Add(button);

            if (i < artistParts.Count - 1)
            {
                var separatorText = new TextBlock
                {
                    Text = Artist.ArtistSeparator.Trim(),
                    Margin = new Thickness(4, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 13
                };

                separatorText.SetBinding(TextBlock.ForegroundProperty, new Binding
                {
                    Source = textBlock, Path = new PropertyPath(nameof(TextBlock.Foreground)), Mode = BindingMode.OneWay
                });

                stackPanel.Children.Add(separatorText);
            }
        }
    }
}

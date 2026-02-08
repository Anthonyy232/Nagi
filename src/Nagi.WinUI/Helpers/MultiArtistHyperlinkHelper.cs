using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Nagi.Core.Models;

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

    public static Song GetSong(DependencyObject obj) => (Song)obj.GetValue(SongProperty);
    public static void SetSong(DependencyObject obj, Song value) => obj.SetValue(SongProperty, value);

    public static ICollection<AlbumArtist> GetAlbumArtists(DependencyObject obj) => (ICollection<AlbumArtist>)obj.GetValue(AlbumArtistsProperty);
    public static void SetAlbumArtists(DependencyObject obj, ICollection<AlbumArtist> value) => obj.SetValue(AlbumArtistsProperty, value);

    public static string GetArtistName(DependencyObject obj) => (string)obj.GetValue(ArtistNameProperty);
    public static void SetArtistName(DependencyObject obj, string value) => obj.SetValue(ArtistNameProperty, value);

    public static ICommand GetCommand(DependencyObject obj) => (ICommand)obj.GetValue(CommandProperty);
    public static void SetCommand(DependencyObject obj, ICommand value) => obj.SetValue(CommandProperty, value);

    private static void OnSongOrArtistNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBlock textBlock)
        {
            // If the control is loaded and has a parent, update immediately
            if (textBlock.IsLoaded && textBlock.Parent != null)
            {
                UpdateHyperlinks(textBlock);
            }
            else
            {
                // Otherwise wait for it to be loaded/parented
                textBlock.Loaded -= TextBlock_Loaded; // Prevent duplicate subscription
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

    private static readonly DependencyProperty LastCacheKeyProperty =
        DependencyProperty.RegisterAttached(
            "LastCacheKey",
            typeof(string),
            typeof(MultiArtistHyperlinkHelper),
            new PropertyMetadata(null));

    private static void UpdateHyperlinks(TextBlock textBlock)
    {
        // Capture dependencies
        var song = GetSong(textBlock);
        var albumArtists = GetAlbumArtists(textBlock);
        var artistString = GetArtistName(textBlock);
        var command = GetCommand(textBlock);

        // Quick escape if nothing changed to prevent rapid-fire redundant updates during virtualization/scrolling
        // Include Command in cache key to ensure we rebuild if command is late-bound!
        string cacheKey = $"{song?.Id ?? Guid.Empty}_{albumArtists?.GetHashCode() ?? 0}_{artistString ?? string.Empty}_{command?.GetHashCode() ?? 0}";
        
        var lastCacheKey = (string)textBlock.GetValue(LastCacheKeyProperty);
        if (lastCacheKey == cacheKey)
        {
            return;
        }
        textBlock.SetValue(LastCacheKeyProperty, cacheKey);
        
        if (textBlock.Parent is not Panel parentPanel)
        {
            // Fallback: if not in a supported container, just set text
            textBlock.Text = artistString ?? string.Empty;
            return;
        }

        // Get or create the StackPanel (cached per TextBlock via attached property)
        var stackPanel = (StackPanel)textBlock.GetValue(AssociatedStackPanelProperty);
        if (stackPanel == null)
        {
            stackPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 0,
                VerticalAlignment = textBlock.VerticalAlignment,
                HorizontalAlignment = textBlock.HorizontalAlignment
            };
            
            // If parent is Grid, copy positioning
            if (parentPanel is Grid)
            {
                Grid.SetColumn(stackPanel, Grid.GetColumn(textBlock));
                Grid.SetRow(stackPanel, Grid.GetRow(textBlock));
                Grid.SetColumnSpan(stackPanel, Grid.GetColumnSpan(textBlock));
                Grid.SetRowSpan(stackPanel, Grid.GetRowSpan(textBlock));
            }
            
            // Hide the original TextBlock
            textBlock.Visibility = Visibility.Collapsed;

            // Insert the new StackPanel at the same index as the TextBlock
            int index = parentPanel.Children.IndexOf(textBlock);
            if (index != -1)
            {
                parentPanel.Children.Insert(index + 1, stackPanel);
            }
            else
            {
                parentPanel.Children.Add(stackPanel);
            }
            
            textBlock.SetValue(AssociatedStackPanelProperty, stackPanel);
        }

        // Clear and rebuild (this is efficient - only happens when content actually changes due to cache check above)
        stackPanel.Children.Clear();

        // Get the secondary text brush for the separator/fallback
        Microsoft.UI.Xaml.Media.Brush? secondaryBrush = null;
        if (Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out var secRes) && secRes is Microsoft.UI.Xaml.Media.Brush secBrush)
        {
            secondaryBrush = secBrush;
        }
        else if (Application.Current.Resources.TryGetValue("TextFillColorSecondary", out var secRes2) && secRes2 is Microsoft.UI.Xaml.Media.Brush secBrush2)
        {
            secondaryBrush = secBrush2;
        }

        if (string.IsNullOrEmpty(artistString))
        {
            stackPanel.Children.Add(new TextBlock 
            { 
                Text = Artist.UnknownArtistName,
                Style = Application.Current.Resources["BodyTextBlockStyle"] as Style,
                Foreground = secondaryBrush
            });
            return;
        }

        // Prefer using structure data from SongArtists if available
        var artistParts = new List<string>();
        
        if (song?.SongArtists?.Any() == true)
        {
            artistParts = song.SongArtists
                .OrderBy(sa => sa.Order)
                .Select(sa => sa.Artist?.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .ToList();
        }
        else if (albumArtists?.Any() == true)
        {
            artistParts = albumArtists
                .OrderBy(aa => aa.Order)
                .Select(aa => aa.Artist?.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .ToList();
        }

        for (int i = 0; i < artistParts.Count; i++)
        {
            var artistPart = artistParts[i];
            
            var button = new HyperlinkButton
            {
                Content = artistPart,
                Command = command,
                CommandParameter = new ArtistNavigationRequest { Song = song, ArtistName = artistPart }
            };

            // Apply the shared style for consistency with album links
            if (Application.Current.Resources.TryGetValue("SongListHyperlinkButtonStyle", out var styleObj) && styleObj is Style style)
            {
                button.Style = style;
            }

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
                
                if (secondaryBrush != null)
                {
                    separatorText.Foreground = secondaryBrush;
                }
                
                stackPanel.Children.Add(separatorText);
            }
        }
    }
}

public record ArtistNavigationRequest
{
    public Song? Song { get; init; }
    public string? ArtistName { get; init; }
}

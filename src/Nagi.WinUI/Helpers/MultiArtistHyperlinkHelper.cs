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

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBlock, StackPanel> _stackPanelCache = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBlock, string> _lastUpdateMap = new();

    private static void UpdateHyperlinks(TextBlock textBlock)
    {
        // Capture dependencies
        var song = GetSong(textBlock);
        var artistString = GetArtistName(textBlock);
        var command = GetCommand(textBlock);

        // Quick escape if nothing changed to prevent rapid-fire redundant updates during virtualization/scrolling
        // Include Command in cache key to ensure we rebuild if command is late-bound!
        string cacheKey = $"{song?.Id ?? Guid.Empty}_{artistString ?? string.Empty}_{command?.GetHashCode() ?? 0}";
        if (_lastUpdateMap.TryGetValue(textBlock, out var lastValue) && lastValue == cacheKey)
        {
            return;
        }
        _lastUpdateMap.Remove(textBlock);
        _lastUpdateMap.Add(textBlock, cacheKey);

        // Instead of using InlineUIContainer (which causes E_INVALIDARG in virtualized lists),
        // we'll replace the TextBlock content with a StackPanel containing HyperlinkButtons.
        // This is more stable and avoids all inline-related crashes.
        
        if (textBlock.Parent is not Panel parentPanel)
        {
            // Fallback: if not in a supported container, just set text
            textBlock.Text = artistString ?? string.Empty;
            return;
        }

        // Get or create the StackPanel (cached per TextBlock)
        if (!_stackPanelCache.TryGetValue(textBlock, out var stackPanel))
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
            
            _stackPanelCache.Add(textBlock, stackPanel);
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
                Text = "Unknown Artist",
                Style = Application.Current.Resources["BodyTextBlockStyle"] as Style,
                Foreground = secondaryBrush
            });
            return;
        }

        // Split by " & " which is Nagi.Core.Models.Artist.ArtistSeparator
        string[] separators = { Artist.ArtistSeparator };
        var parts = artistString.Split(separators, StringSplitOptions.None);

        for (int i = 0; i < parts.Length; i++)
        {
            var artistPart = parts[i].Trim();
            if (string.IsNullOrEmpty(artistPart)) continue;

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

            if (i < parts.Length - 1)
            {
                // Add the separator as a TextBlock with explicit margins
                // This is more reliable than trailing spaces in WinUI TextBlocks
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

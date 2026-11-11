using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.Controls;

/// <summary>
///     Square artwork, or a placeholder icon when there is none. The corners are rounded like other artwork, or
///     form a circle that follows the size.
/// </summary>
public sealed partial class ArtworkImage : UserControl
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(string), typeof(ArtworkImage), new PropertyMetadata(null));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(ArtworkImage), new PropertyMetadata(0d, OnShapeChanged));

    public static readonly DependencyProperty IsCircularProperty = DependencyProperty.Register(
        nameof(IsCircular), typeof(bool), typeof(ArtworkImage), new PropertyMetadata(false, OnShapeChanged));

    public static readonly DependencyProperty PlaceholderGlyphProperty = DependencyProperty.Register(
        nameof(PlaceholderGlyph), typeof(string), typeof(ArtworkImage), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty PlaceholderGlyphSizeProperty = DependencyProperty.Register(
        nameof(PlaceholderGlyphSize), typeof(double), typeof(ArtworkImage), new PropertyMetadata(48d));

    public ArtworkImage()
    {
        InitializeComponent();
    }

    /// <summary>The artwork's URI; null or empty shows the placeholder.</summary>
    public string? Source
    {
        get => (string?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>The width and height, in logical pixels; the image is decoded at this size.</summary>
    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public bool IsCircular
    {
        get => (bool)GetValue(IsCircularProperty);
        set => SetValue(IsCircularProperty, value);
    }

    public string PlaceholderGlyph
    {
        get => (string)GetValue(PlaceholderGlyphProperty);
        set => SetValue(PlaceholderGlyphProperty, value);
    }

    public double PlaceholderGlyphSize
    {
        get => (double)GetValue(PlaceholderGlyphSizeProperty);
        set => SetValue(PlaceholderGlyphSizeProperty, value);
    }

    private ImageSource? Decode(string? source, double size) =>
        BitmapImageFactory.CreateDecoded(source, (int)Math.Round(size));

    private Visibility ImageVisibility(string? source) =>
        string.IsNullOrEmpty(source) ? Visibility.Collapsed : Visibility.Visible;

    private Visibility PlaceholderVisibility(string? source) =>
        string.IsNullOrEmpty(source) ? Visibility.Visible : Visibility.Collapsed;

    private static void OnShapeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var artwork = (ArtworkImage)d;
        if (!artwork.IsCircular) return;
        var radius = new CornerRadius(artwork.Size / 2);
        artwork.Image.CornerRadius = radius;
        artwork.Placeholder.CornerRadius = radius;
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Nagi.WinUI.Controls;

/// <summary>
///     The top of a page that shows one album, artist, playlist, genre or folder: its artwork beside a caption and its name.
/// </summary>
public sealed partial class DetailPageHeader : UserControl
{
    public static readonly DependencyProperty ArtworkProperty = DependencyProperty.Register(
        nameof(Artwork), typeof(UIElement), typeof(DetailPageHeader), new PropertyMetadata(null));

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(DetailPageHeader), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(DetailPageHeader), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DetailsProperty = DependencyProperty.Register(
        nameof(Details), typeof(UIElement), typeof(DetailPageHeader), new PropertyMetadata(null));

    public DetailPageHeader()
    {
        InitializeComponent();
    }

    public UIElement? Artwork
    {
        get => (UIElement?)GetValue(ArtworkProperty);
        set => SetValue(ArtworkProperty, value);
    }

    /// <summary>What kind of thing the page shows, such as "Album".</summary>
    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>An optional line under the name.</summary>
    public UIElement? Details
    {
        get => (UIElement?)GetValue(DetailsProperty);
        set => SetValue(DetailsProperty, value);
    }

    private Visibility DetailsVisibility(UIElement? details) => details is null ? Visibility.Collapsed : Visibility.Visible;
}

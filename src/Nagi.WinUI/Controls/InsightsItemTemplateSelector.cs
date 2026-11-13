using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.Presentation.Insights;

namespace Nagi.WinUI.Controls;

public class InsightsItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? SongTemplate { get; set; }
    public DataTemplate? ArtistTemplate { get; set; }
    public DataTemplate? AlbumTemplate { get; set; }
    public DataTemplate? GenreTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        TopSongItem => SongTemplate,
        TopArtistItem => ArtistTemplate,
        TopAlbumItem => AlbumTemplate,
        TopGenreItem => GenreTemplate,
        _ => base.SelectTemplateCore(item)
    };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

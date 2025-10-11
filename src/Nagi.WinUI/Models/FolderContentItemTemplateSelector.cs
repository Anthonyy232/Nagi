using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Nagi.WinUI.Models;

/// <summary>
///     Selects the appropriate data template for a FolderContentItem based on whether it's a folder or a song.
/// </summary>
public class FolderContentItemTemplateSelector : DataTemplateSelector
{
    /// <summary>
    ///     Gets or sets the template to use for folder items.
    /// </summary>
    public DataTemplate? FolderTemplate { get; set; }

    /// <summary>
    ///     Gets or sets the template to use for song items.
    /// </summary>
    public DataTemplate? SongTemplate { get; set; }

    /// <summary>
    ///     Selects the appropriate template based on the item type.
    /// </summary>
    protected override DataTemplate? SelectTemplateCore(object item)
    {
        if (item is FolderContentItem contentItem)
        {
            return contentItem.IsFolder ? FolderTemplate : SongTemplate;
        }

        return base.SelectTemplateCore(item);
    }

    /// <summary>
    ///     Selects the appropriate template based on the item type (overload with container parameter).
    /// </summary>
    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
    {
        return SelectTemplateCore(item);
    }
}

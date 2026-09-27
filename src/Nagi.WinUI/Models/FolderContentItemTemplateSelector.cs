using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Nagi.WinUI.Models;

public class FolderContentItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? FolderTemplate { get; set; }
    public DataTemplate? SongTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
    {
        if (item is FolderContentItem contentItem) return contentItem.IsFolder ? FolderTemplate : SongTemplate;

        return base.SelectTemplateCore(item);
    }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

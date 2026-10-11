using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.WinUI.Models;

namespace Nagi.WinUI.Helpers;

public sealed class RecentHistoryTemplateSelector : DataTemplateSelector
{
    public DataTemplate? HeaderTemplate { get; set; }
    public DataTemplate? SongTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is RecentListenDateHeader ? HeaderTemplate : SongTemplate;
}

public sealed class RecentHistoryStyleSelector : StyleSelector
{
    public Style? HeaderStyle { get; set; }
    public Style? SongStyle { get; set; }

    protected override Style? SelectStyleCore(object item, DependencyObject container) =>
        item is RecentListenDateHeader ? HeaderStyle : SongStyle;
}

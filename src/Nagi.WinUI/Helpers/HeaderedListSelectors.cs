using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.Presentation.Models;

namespace Nagi.WinUI.Helpers;

/// <summary>
///     Picks the header or song template for lists that group songs under <see cref="IListHeader" /> rows.
/// </summary>
public sealed class HeaderedListTemplateSelector : DataTemplateSelector
{
    public DataTemplate? HeaderTemplate { get; set; }
    public DataTemplate? SongTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is IListHeader ? HeaderTemplate : SongTemplate;
}

/// <summary>
///     Picks the header or song container style for lists that group songs under <see cref="IListHeader" /> rows.
/// </summary>
public sealed class HeaderedListStyleSelector : StyleSelector
{
    public Style? HeaderStyle { get; set; }
    public Style? SongStyle { get; set; }

    protected override Style? SelectStyleCore(object item, DependencyObject container) =>
        item is IListHeader ? HeaderStyle : SongStyle;
}

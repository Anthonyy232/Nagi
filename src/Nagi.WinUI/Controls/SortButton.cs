using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Nagi.Presentation.ViewModels;

namespace Nagi.WinUI.Controls;

/// <summary>
///     A list's sort button: its menu offers the list's sort orders, and its tooltip names the current one.
/// </summary>
public sealed class SortButton : Button
{
    public static readonly DependencyProperty SelectionProperty = DependencyProperty.Register(
        nameof(Selection), typeof(ISortOrderSelection), typeof(SortButton), new PropertyMetadata(null, OnSelectionChanged));

    private readonly MenuFlyout _menu = new() { Placement = FlyoutPlacementMode.Bottom };
    private readonly string _groupName = Guid.NewGuid().ToString();

    public SortButton()
    {
        Style = (Style)Application.Current.Resources["SongListViewPageIconButton"];
        Content = new FontIcon { FontSize = 16, Glyph = "" };
        Flyout = _menu;
        _menu.Opening += (_, _) => FillMenu();
    }

    public ISortOrderSelection? Selection
    {
        get => (ISortOrderSelection?)GetValue(SelectionProperty);
        set => SetValue(SelectionProperty, value);
    }

    private static void OnSelectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var button = (SortButton)d;
        var text = new Binding { Source = e.NewValue, Path = new PropertyPath(nameof(ISortOrderSelection.Text)) };
        button.SetBinding(ToolTipService.ToolTipProperty, text);
        button.SetBinding(AutomationProperties.NameProperty, text);
    }

    // Built each time it opens, so the checked order is always the current one.
    private void FillMenu()
    {
        _menu.Items.Clear();
        if (Selection is not { } selection) return;
        foreach (var option in selection.Options)
            _menu.Items.Add(new RadioMenuFlyoutItem
            {
                Text = option.Label,
                GroupName = _groupName,
                IsChecked = Equals(option.Order, selection.Current),
                Command = selection.ChangeCommand,
                CommandParameter = option.Order
            });
    }
}

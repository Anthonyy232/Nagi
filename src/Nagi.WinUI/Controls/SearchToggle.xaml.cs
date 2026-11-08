using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Nagi.Presentation.Resources;
using Windows.System;

namespace Nagi.WinUI.Controls;

/// <summary>
///     A list's search box, hidden behind a toggle button. Opening it focuses the box; closing it, or Escape, clears
///     the search. The button's tooltip and accessible name follow the state. Each time the control loads, the box
///     opens if the list still has a search term, so cached pages come back in a consistent state.
/// </summary>
public sealed partial class SearchToggle : UserControl
{
    public static readonly DependencyProperty SearchTermProperty = DependencyProperty.Register(
        nameof(SearchTerm), typeof(string), typeof(SearchToggle),
        new PropertyMetadata(string.Empty, (d, e) => ((SearchToggle)d).OnSearchTermChanged((string)e.NewValue)));

    public static readonly DependencyProperty PlaceholderTextProperty = DependencyProperty.Register(
        nameof(PlaceholderText), typeof(string), typeof(SearchToggle), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SearchToolTipProperty = DependencyProperty.Register(
        nameof(SearchToolTip), typeof(string), typeof(SearchToggle), new PropertyMetadata(string.Empty));

    private bool _isExpanded;

    public SearchToggle()
    {
        InitializeComponent();
        Loaded += (_, _) => SetExpanded(!string.IsNullOrWhiteSpace(SearchTerm));
    }

    public string SearchTerm
    {
        get => (string)GetValue(SearchTermProperty);
        set => SetValue(SearchTermProperty, value);
    }

    public string PlaceholderText
    {
        get => (string)GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    /// <summary>The toggle button's tooltip and name while the box is closed.</summary>
    public string SearchToolTip
    {
        get => (string)GetValue(SearchToolTipProperty);
        set => SetValue(SearchToolTipProperty, value);
    }

    // A term that differs from the box came from the list, which may clear its own search, for example when the
    // panel showing it closes. Emptying the box by typing leaves it open.
    private void OnSearchTermChanged(string term)
    {
        if (term == SearchTextBox.Text) return;
        SearchTextBox.Text = term;
        if (_isExpanded && string.IsNullOrEmpty(term)) SetExpanded(false);
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e) => SearchTerm = SearchTextBox.Text;

    private void SetExpanded(bool expanded)
    {
        _isExpanded = expanded;
        var label = expanded ? Strings.Generic_CloseSearch : SearchToolTip;
        ToolTipService.SetToolTip(SearchToggleButton, label);
        AutomationProperties.SetName(SearchToggleButton, label);
        VisualStateManager.GoToState(this, expanded ? "SearchExpanded" : "SearchCollapsed", false);
    }

    private void Collapse()
    {
        if (!_isExpanded) return;
        SetExpanded(false);
        SearchToggleButton.Focus(FocusState.Programmatic);
        SearchTerm = string.Empty;
    }

    private void SearchToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isExpanded)
        {
            Collapse();
            return;
        }

        SetExpanded(true);
        SearchTextBox.Focus(FocusState.Programmatic);
    }

    private void SearchTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        Collapse();
        e.Handled = true;
    }
}

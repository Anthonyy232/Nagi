using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.Controls;

/// <summary>
///     A list's search box, hidden behind a toggle button. Each time the page loads, the box opens if the list still
///     has a search term, so cached pages come back in a consistent state.
/// </summary>
public sealed partial class SearchToggle : UserControl
{
    public static readonly DependencyProperty SearchTermProperty = DependencyProperty.Register(
        nameof(SearchTerm), typeof(string), typeof(SearchToggle), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty PlaceholderTextProperty = DependencyProperty.Register(
        nameof(PlaceholderText), typeof(string), typeof(SearchToggle), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SearchToolTipProperty = DependencyProperty.Register(
        nameof(SearchToolTip), typeof(string), typeof(SearchToggle), new PropertyMetadata(string.Empty));

    private CollapsibleSearch? _search;

    public SearchToggle()
    {
        InitializeComponent();
        Loaded += OnLoaded;
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

    private CollapsibleSearch Search => _search ??= new CollapsibleSearch(this, SearchToggleButton, SearchTextBox,
        SearchToolTip, "SearchExpanded", "SearchCollapsed", () => SearchTerm = string.Empty);

    private void OnLoaded(object sender, RoutedEventArgs e) => Search.SetExpanded(!string.IsNullOrWhiteSpace(SearchTerm));

    private void SearchToggleButton_Click(object sender, RoutedEventArgs e) => Search.Toggle();

    private void SearchTextBox_KeyDown(object sender, KeyRoutedEventArgs e) => Search.HandleKeyDown(e);
}

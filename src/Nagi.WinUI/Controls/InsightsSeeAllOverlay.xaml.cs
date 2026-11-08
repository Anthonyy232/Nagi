using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Nagi.Presentation.Insights;
using Nagi.Presentation.Resources;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.Controls;

/// <summary>
///     The Insights "See all" panel. The shell hosts it so that it covers the whole window, player included.
/// </summary>
public sealed partial class InsightsSeeAllOverlay : UserControl
{
    private readonly CollapsibleSearch _search;

    public InsightsSeeAllOverlay()
    {
        ViewModel = App.Services!.GetRequiredService<InsightsViewModel>().SeeAll;
        InitializeComponent();
        _search = new CollapsibleSearch(this, SeeAllSearchToggleButton, SeeAllSearchTextBox,
            Strings.InsightsPage_SeeAll_SearchButton_ToolTip, "SeeAllSearchExpanded", "SeeAllSearchCollapsed",
            () => ViewModel.SearchTerm = string.Empty);

        Loaded += (_, _) => ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Unloaded += (_, _) => ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    public InsightsSeeAllViewModel ViewModel { get; }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InsightsSeeAllViewModel.IsOpen) && !ViewModel.IsOpen)
            _search.SetExpanded(false);
    }

    private void OnBackdropTapped(object sender, TappedRoutedEventArgs e) => ViewModel.CloseCommand.Execute(null);

    private void OnPanelTapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    private void OnSearchToggleButtonClick(object sender, RoutedEventArgs e) => _search.Toggle();

    private void OnSearchTextBoxKeyDown(object sender, KeyRoutedEventArgs e) => _search.HandleKeyDown(e);
}

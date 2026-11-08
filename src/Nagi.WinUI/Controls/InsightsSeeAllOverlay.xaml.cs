using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Nagi.Presentation.Insights;

namespace Nagi.WinUI.Controls;

/// <summary>
///     The Insights "See all" panel. The shell hosts it so that it covers the whole window, player included.
/// </summary>
public sealed partial class InsightsSeeAllOverlay : UserControl
{
    public InsightsSeeAllOverlay(InsightsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public InsightsViewModel ViewModel { get; }

    private void OnBackdropTapped(object sender, TappedRoutedEventArgs e) => ViewModel.CloseSeeAllCommand.Execute(null);

    private void OnPanelTapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;
}


using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Presentation.Insights;

namespace Nagi.WinUI.Pages;

public sealed partial class InsightsPage : ViewModelPage
{
    public InsightsPage()
    {
        InitializeComponent();
        ViewModel = GetViewModel<InsightsViewModel>();
    }

    public InsightsViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadInsightsAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.SeeAll.CloseCommand.Execute(null);
    }
}

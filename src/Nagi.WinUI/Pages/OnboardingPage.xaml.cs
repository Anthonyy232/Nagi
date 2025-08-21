using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.WinUI.Controls;
using Nagi.WinUI.ViewModels;

namespace Nagi.WinUI.Pages;

/// <summary>
///     A page that prompts the user to add their initial music library folder.
///     It manages the UI state based on background operations in its ViewModel.
/// </summary>
public sealed partial class OnboardingPage : Page, ICustomTitleBarProvider
{
    public OnboardingPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<OnboardingViewModel>();
        DataContext = ViewModel;
        Loaded += OnboardingPage_Loaded;
        Unloaded += OnboardingPage_Unloaded;
    }

    /// <summary>
    ///     Gets the view model associated with this page.
    /// </summary>
    public OnboardingViewModel ViewModel { get; }

    /// <summary>
    ///     Provides access to the element that serves as the custom title bar.
    /// </summary>
    public TitleBar GetAppTitleBarElement()
    {
        return AppTitleBar;
    }

    /// <summary>
    ///     Provides access to the RowDefinition for the custom title bar.
    /// </summary>
    public RowDefinition GetAppTitleBarRowElement()
    {
        return AppTitleBarRow;
    }

    private void OnboardingPage_Loaded(object sender, RoutedEventArgs e)
    {
        // Trigger the entrance animation when the page is loaded.
        VisualStateManager.GoToState(this, "PageLoaded", true);

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateVisualState(ViewModel.IsAnyOperationInProgress);
    }

    private void OnboardingPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }

    /// <summary>
    ///     Listens for ViewModel property changes to trigger UI state transitions.
    /// </summary>
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.IsAnyOperationInProgress))
            DispatcherQueue.TryEnqueue(() => { UpdateVisualState(ViewModel.IsAnyOperationInProgress); });
    }

    /// <summary>
    ///     Transitions the page between the 'Idle' and 'Working' visual states
    ///     based on whether an operation is in progress.
    /// </summary>
    private void UpdateVisualState(bool isWorking)
    {
        var stateName = isWorking ? "Working" : "Idle";
        VisualStateManager.GoToState(this, stateName, true);
    }
}
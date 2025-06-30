using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.Interfaces;
using Nagi.ViewModels;

namespace Nagi.Pages;

/// <summary>
///     A page that prompts the user to add their initial music library folder.
///     It manages the UI state based on background operations in its ViewModel.
/// </summary>
public sealed partial class OnboardingPage : Page, ICustomTitleBarProvider {
    public OnboardingPage() {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<OnboardingViewModel>();
        DataContext = ViewModel;
        Loaded += OnboardingPage_Loaded;
        Unloaded += OnboardingPage_Unloaded;
    }

    /// <summary>
    ///     Gets the view model associated with this page.
    /// </summary>
    public OnboardingViewModel ViewModel { get; }

    /// <summary>
    ///     Provides access to the TitleBar element that serves as the custom title bar.
    /// </summary>
    public TitleBar GetAppTitleBarElement() {
        return AppTitleBar;
    }

    /// <summary>
    ///     Provides access to the RowDefinition for the custom title bar.
    /// </summary>
    public RowDefinition GetAppTitleBarRowElement() {
        return AppTitleBarRow;
    }

    private void OnboardingPage_Loaded(object sender, RoutedEventArgs e) {
        // Trigger the entrance animation when the page is loaded.
        VisualStateManager.GoToState(this, "PageLoaded", true);

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        // Set the initial visual state based on the view model's current status.
        UpdateVisualState(ViewModel.IsAnyOperationInProgress);
    }

    private void OnboardingPage_Unloaded(object sender, RoutedEventArgs e) {
        // Unsubscribe from the event to prevent memory leaks when the page is unloaded.
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }

    /// <summary>
    ///     Listens for ViewModel property changes to trigger UI state transitions.
    /// </summary>
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName == nameof(ViewModel.IsAnyOperationInProgress))
            UpdateVisualState(ViewModel.IsAnyOperationInProgress);
    }

    /// <summary>
    ///     Transitions the page between the 'Idle' and 'Working' visual states
    ///     based on whether an operation is in progress.
    /// </summary>
    private void UpdateVisualState(bool isWorking) {
        var stateName = isWorking ? "Working" : "Idle";
        VisualStateManager.GoToState(this, stateName, true);
    }
}
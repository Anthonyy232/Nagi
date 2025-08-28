using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.WinUI.Controls;
using Nagi.WinUI.ViewModels;

namespace Nagi.WinUI.Pages;

/// <summary>
///     A page that prompts the user to add their initial music library folder.
/// </summary>
public sealed partial class OnboardingPage : Page, ICustomTitleBarProvider {
    private readonly ILogger<OnboardingPage> _logger;

    public OnboardingPage() {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<OnboardingViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<OnboardingPage>>();
        DataContext = ViewModel;
        Loaded += OnboardingPage_Loaded;
        Unloaded += OnboardingPage_Unloaded;
        _logger.LogInformation("OnboardingPage initialized.");
    }

    /// <summary>
    ///     Gets the view model associated with this page.
    /// </summary>
    public OnboardingViewModel ViewModel { get; }

    public TitleBar GetAppTitleBarElement() => AppTitleBar;
    public RowDefinition GetAppTitleBarRowElement() => AppTitleBarRow;

    private void OnboardingPage_Loaded(object sender, RoutedEventArgs e) {
        _logger.LogInformation("OnboardingPage loaded.");
        VisualStateManager.GoToState(this, "PageLoaded", true);
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateVisualState(ViewModel.IsAnyOperationInProgress);
    }

    private void OnboardingPage_Unloaded(object sender, RoutedEventArgs e) {
        _logger.LogInformation("OnboardingPage unloaded.");
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }

    /// <summary>
    ///     Listens for ViewModel property changes to trigger UI state transitions.
    /// </summary>
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName == nameof(ViewModel.IsAnyOperationInProgress))
            DispatcherQueue.TryEnqueue(() => { UpdateVisualState(ViewModel.IsAnyOperationInProgress); });
    }

    /// <summary>
    ///     Transitions the page between the 'Idle' and 'Working' visual states.
    /// </summary>
    private void UpdateVisualState(bool isWorking) {
        var stateName = isWorking ? "Working" : "Idle";
        _logger.LogDebug("Updating visual state to '{StateName}'.", stateName);
        VisualStateManager.GoToState(this, stateName, true);
    }
}
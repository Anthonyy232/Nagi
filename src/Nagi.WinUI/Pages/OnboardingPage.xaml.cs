using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.WinUI.Controls;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.ViewModels;

namespace Nagi.WinUI.Pages;

public sealed partial class OnboardingPage : Page, ICustomTitleBarProvider
{
    private readonly ILogger<OnboardingPage> _logger;
    private bool _isUnloaded;

    public OnboardingPage(OnboardingViewModel viewModel, ILogger<OnboardingPage> logger)
    {
        InitializeComponent();
        ViewModel = viewModel;
        _logger = logger;
        DataContext = ViewModel;
        Loaded += OnboardingPage_Loaded;
        Unloaded += OnboardingPage_Unloaded;
        _logger.LogDebug("OnboardingPage initialized.");
    }

    public OnboardingViewModel ViewModel { get; }

    public TitleBar GetAppTitleBarElement()
    {
        return AppTitleBar;
    }

    private void OnboardingPage_Loaded(object sender, RoutedEventArgs e)
    {
        _isUnloaded = false;
        _logger.LogDebug("OnboardingPage loaded.");
        VisualStateManager.GoToState(this, "PageLoaded", CompositionAnimationHelper.AreAnimationsEnabled);
        if (!CompositionAnimationHelper.AreAnimationsEnabled) EntranceStates.CurrentState?.Storyboard?.SkipToFill();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateVisualState(ViewModel.IsAnyOperationInProgress);
    }

    private void OnboardingPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _isUnloaded = true;
        _logger.LogDebug("OnboardingPage unloaded.");
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.IsAnyOperationInProgress))
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isUnloaded) return;
                UpdateVisualState(ViewModel.IsAnyOperationInProgress);
            });
    }

    private void UpdateVisualState(bool isWorking)
    {
        var stateName = isWorking ? "Working" : "Idle";
        _logger.LogDebug("Updating visual state to '{StateName}'.", stateName);
        VisualStateManager.GoToState(this, stateName, CompositionAnimationHelper.AreAnimationsEnabled);
        if (!CompositionAnimationHelper.AreAnimationsEnabled) ActivityStates.CurrentState?.Storyboard?.SkipToFill();
    }
}

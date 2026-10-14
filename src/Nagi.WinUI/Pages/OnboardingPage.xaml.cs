using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.Presentation.ViewModels;
using Nagi.WinUI.Controls;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.Pages;

public sealed partial class OnboardingPage : Page, ICustomTitleBarProvider
{
    private bool _isUnloaded;

    public OnboardingPage(OnboardingViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = ViewModel;
        Loaded += OnboardingPage_Loaded;
        Unloaded += OnboardingPage_Unloaded;
    }

    public OnboardingViewModel ViewModel { get; }

    public TitleBar GetAppTitleBarElement()
    {
        return AppTitleBar;
    }

    private void OnboardingPage_Loaded(object sender, RoutedEventArgs e)
    {
        _isUnloaded = false;
        VisualStateManager.GoToState(this, "PageLoaded", CompositionAnimationHelper.AreAnimationsEnabled);
        if (!CompositionAnimationHelper.AreAnimationsEnabled) EntranceStates.CurrentState?.Storyboard?.SkipToFill();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateVisualState(ViewModel.IsAnyOperationInProgress);
    }

    private void OnboardingPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _isUnloaded = true;
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
        VisualStateManager.GoToState(this, stateName, CompositionAnimationHelper.AreAnimationsEnabled);
        if (!CompositionAnimationHelper.AreAnimationsEnabled) ActivityStates.CurrentState?.Storyboard?.SkipToFill();
    }
}

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Numerics;
using CommunityToolkit.WinUI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Nagi.Presentation.ViewModels;
using Nagi.WinUI.Controls;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.Pages;

public sealed partial class OnboardingPage : Page, ICustomTitleBarProvider
{
    private const float StepSlideOffset = 32f;
    private const int StepTransitionMs = 500;
    private const int StepStaggerMs = 60;
    private const int StepExitMs = 150;

    private readonly Dictionary<OnboardingStep, FrameworkElement> _steps;
    private OnboardingStep _shownStep;

    public OnboardingPage(OnboardingViewModel viewModel, SettingsViewModel settings)
    {
        InitializeComponent();
        ViewModel = viewModel;
        Settings = settings;

        _steps = new Dictionary<OnboardingStep, FrameworkElement>
        {
            [OnboardingStep.Welcome] = WelcomeScroller,
            [OnboardingStep.Library] = LibraryStep,
            [OnboardingStep.Appearance] = AppearanceStep,
            [OnboardingStep.Listening] = ListeningStep,
            [OnboardingStep.OnlineFeatures] = OnlineFeaturesStep,
            [OnboardingStep.Desktop] = DesktopStep,
            [OnboardingStep.Sharing] = SharingStep,
            [OnboardingStep.Finish] = FinishStep
        };
        // The welcome step has no footer, so the pips cover the steps after it.
        StepPips.NumberOfPages = _steps.Count - 1;
        StepPips.MaxVisiblePips = _steps.Count - 1;

        Loaded += OnboardingPage_Loaded;
        Unloaded += OnboardingPage_Unloaded;
    }

    public OnboardingViewModel ViewModel { get; }

    public SettingsViewModel Settings { get; }

    public TitleBar GetAppTitleBarElement() => AppTitleBar;

    private async void OnboardingPage_Loaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ShowStep(ViewModel.CurrentStep, new Vector3(0f, StepSlideOffset / 2, 0f));

        await Settings.LoadSettingsAsync();
    }

    private void OnboardingPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OnboardingViewModel.CurrentStep)) return;

        // The outgoing step fades out while the next one lays out and starts to enter. Swapping them at once
        // leaves the screen empty for the few frames the new step takes to lay out, which reads as a blink.
        var direction = ViewModel.CurrentStep > _shownStep ? 1f : -1f;
        var outgoing = _steps[_shownStep];
        outgoing.IsHitTestVisible = false;
        ShowStep(ViewModel.CurrentStep, new Vector3(direction * StepSlideOffset, 0f, 0f), StepExitMs / 2);

        CompositionAnimationHelper.AnimateSlideOut(outgoing, new Vector3(-direction * StepSlideOffset / 2, 0f, 0f),
            StepExitMs, () =>
            {
                // The user may have returned to this step while it was leaving.
                if (outgoing != _steps[_shownStep]) outgoing.Visibility = Visibility.Collapsed;
            });

        // The footer leaves with the last step before the welcome step and collapses once it has faded, so
        // the step area does not resize under the step still fading out.
        if (_shownStep == OnboardingStep.Welcome)
        {
            Footer.IsHitTestVisible = false;
            CompositionAnimationHelper.AnimateSlideOut(Footer, new Vector3(0f, StepSlideOffset / 2, 0f), StepExitMs,
                () =>
                {
                    if (_shownStep == OnboardingStep.Welcome) Footer.Visibility = Visibility.Collapsed;
                });
        }
    }

    private void ShowStep(OnboardingStep step, Vector3 enterOffset, int entranceDelayMs = 0)
    {
        // Focus inside the outgoing step, or in the footer when it is about to leave, would be lost when they
        // collapse, so it moves into the new step.
        var outgoing = _steps[_shownStep];
        var lostFocus = FocusManager.GetFocusedElement(XamlRoot) is UIElement focused &&
                        focused.FindAscendantOrSelf<DependencyObject>(ancestor =>
                            ancestor == outgoing || (step == OnboardingStep.Welcome && ancestor == Footer)) is not null
            ? focused.FocusState
            : FocusState.Unfocused;

        // The footer appears when leaving the welcome step, so it enters with the step after it.
        if (_shownStep == OnboardingStep.Welcome && step != OnboardingStep.Welcome)
        {
            Footer.IsHitTestVisible = true;
            Footer.Visibility = Visibility.Visible;
            CompositionAnimationHelper.AnimateSlideIn(Footer, new Vector3(0f, StepSlideOffset / 2, 0f),
                StepTransitionMs, entranceDelayMs + StepStaggerMs * 2);
        }

        var element = _steps[step];
        // A step that left earlier is still faded out from its exit; its parts enter individually instead.
        CompositionAnimationHelper.ResetSlide(element);
        element.IsHitTestVisible = true;
        element.Visibility = Visibility.Visible;
        _shownStep = step;

        StepPips.SelectedPageIndex = Math.Max(0, (int)step - 1);
        StepScroller.ChangeView(null, 0, null, true);
        var delayMs = entranceDelayMs;
        foreach (var part in GetEntranceElements(element))
        {
            CompositionAnimationHelper.AnimateSlideIn(part, enterOffset, StepTransitionMs, delayMs);
            delayMs += StepStaggerMs;
        }

        if (lostFocus == FocusState.Unfocused) return;
        // Deferred until the step has been laid out, since a collapsed step has nothing focusable, and the
        // commands have caught up with the new step, since its buttons are still disabled for the old one.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            var shown = _steps[_shownStep];
            shown.UpdateLayout();
            if (FocusManager.FindFirstFocusableElement(shown) is UIElement target) target.Focus(lostFocus);
        });
    }

    // Each part of a step enters a beat after the one above it, so the step reads top to bottom.
    private IEnumerable<UIElement> GetEntranceElements(FrameworkElement step) =>
        step is OnboardingStepView stepView ? stepView.EntranceElements : WelcomeStep.Children;

    private void StepPips_SelectedIndexChanged(PipsPager sender, PipsPagerSelectedIndexChangedEventArgs args)
    {
        // ShowStep moves the pips too, and parks them on the first pip while the welcome step is shown.
        var step = (OnboardingStep)(sender.SelectedPageIndex + 1);
        if (step == ViewModel.CurrentStep || ViewModel.CurrentStep == OnboardingStep.Welcome) return;

        if (ViewModel.GoToStepCommand.CanExecute(step)) ViewModel.GoToStepCommand.Execute(step);
        else sender.SelectedPageIndex = (int)ViewModel.CurrentStep - 1;
    }
}

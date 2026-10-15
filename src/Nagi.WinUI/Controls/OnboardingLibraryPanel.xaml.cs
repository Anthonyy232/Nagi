using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.Presentation.ViewModels;

namespace Nagi.WinUI.Controls;

/// <summary>
///     The music folder chosen during onboarding and the progress of building the library from it, or
///     the button to choose one when there is none yet or the last attempt failed.
/// </summary>
public sealed partial class OnboardingLibraryPanel : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel),
        typeof(OnboardingViewModel),
        typeof(OnboardingLibraryPanel),
        new PropertyMetadata(null));

    public OnboardingLibraryPanel()
    {
        InitializeComponent();
    }

    public OnboardingViewModel? ViewModel
    {
        get => (OnboardingViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }
}

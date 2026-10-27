using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace Nagi.WinUI.Controls;

/// <summary>
///     Lays out one onboarding step: its title and description beside the step's controls on wide
///     windows, and above them on narrow ones.
/// </summary>
[ContentProperty(Name = nameof(Body))]
public sealed partial class OnboardingStepView : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(OnboardingStepView), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(OnboardingStepView), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty BodyProperty = DependencyProperty.Register(
        nameof(Body), typeof(object), typeof(OnboardingStepView), new PropertyMetadata(null));

    public OnboardingStepView()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public object? Body
    {
        get => GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    /// <summary>
    ///     The parts of the step in the order they enter: the title, the description, then each item of a
    ///     panel body (such as one settings card after another) or the whole body otherwise.
    /// </summary>
    public IEnumerable<UIElement> EntranceElements
    {
        get
        {
            yield return TitleText;
            yield return DescriptionText;
            if (Body is Panel panel)
                foreach (var child in panel.Children) yield return child;
            else
                yield return BodyPresenter;
        }
    }
}

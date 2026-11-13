using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.Presentation.Resources;

namespace Nagi.WinUI.Controls;

/// <summary>
///     What a list shows in place of its items: why it is empty once a load found nothing, or that the load failed.
/// </summary>
public sealed partial class EmptyState : UserControl
{
    private const string ErrorGlyph = "\uE783";

    public static readonly DependencyProperty GlyphProperty = Register(nameof(Glyph), string.Empty);
    public static readonly DependencyProperty TitleProperty = Register(nameof(Title), string.Empty);
    public static readonly DependencyProperty SubtitleProperty = Register(nameof(Subtitle), string.Empty);
    public static readonly DependencyProperty IsShownProperty = Register(nameof(IsShown), false);
    public static readonly DependencyProperty HasLoadErrorProperty = Register(nameof(HasLoadError), false);

    public static readonly DependencyProperty ActionProperty = DependencyProperty.Register(
        nameof(Action), typeof(UIElement), typeof(EmptyState), new PropertyMetadata(null, (d, _) => ((EmptyState)d).Update()));

    public EmptyState()
    {
        InitializeComponent();
        Update();
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    /// <summary>Whether the list finished loading and has nothing to show.</summary>
    public bool IsShown
    {
        get => (bool)GetValue(IsShownProperty);
        set => SetValue(IsShownProperty, value);
    }

    public bool HasLoadError
    {
        get => (bool)GetValue(HasLoadErrorProperty);
        set => SetValue(HasLoadErrorProperty, value);
    }

    /// <summary>A button that helps fill the empty list, hidden while the error shows.</summary>
    public UIElement? Action
    {
        get => (UIElement?)GetValue(ActionProperty);
        set => SetValue(ActionProperty, value);
    }

    private static DependencyProperty Register<T>(string name, T defaultValue) =>
        DependencyProperty.Register(name, typeof(T), typeof(EmptyState),
            new PropertyMetadata(defaultValue, (d, _) => ((EmptyState)d).Update()));

    private void Update()
    {
        var hasError = HasLoadError;
        Icon.Glyph = hasError ? ErrorGlyph : Glyph;
        TitleText.Text = hasError ? Strings.Generic_Error : Title;
        SubtitleText.Text = hasError ? Strings.Generic_LoadError : Subtitle;
        ActionHost.Content = hasError ? null : Action;
        Visibility = IsShown || hasError ? Visibility.Visible : Visibility.Collapsed;
    }
}

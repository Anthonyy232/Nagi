using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Nagi.Presentation.Resources;

namespace Nagi.WinUI.Controls;

/// <summary>
///     What a list shows in place of its items: why it is empty once a load found nothing, or that the load failed.
/// </summary>
public sealed class EmptyState : UserControl
{
    private const string ErrorGlyph = "";

    public static readonly DependencyProperty GlyphProperty = Register(nameof(Glyph), string.Empty);
    public static readonly DependencyProperty TitleProperty = Register(nameof(Title), string.Empty);
    public static readonly DependencyProperty SubtitleProperty = Register(nameof(Subtitle), string.Empty);
    public static readonly DependencyProperty IsShownProperty = Register(nameof(IsShown), false);
    public static readonly DependencyProperty HasLoadErrorProperty = Register(nameof(HasLoadError), false);

    public static readonly DependencyProperty ActionProperty = DependencyProperty.Register(
        nameof(Action), typeof(UIElement), typeof(EmptyState), new PropertyMetadata(null, (d, _) => ((EmptyState)d).Update()));

    private readonly FontIcon _icon = new()
    {
        FontSize = 48,
        Margin = new Thickness(0, 0, 0, 12),
        HorizontalAlignment = HorizontalAlignment.Center
    };

    private readonly TextBlock _title = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _subtitle = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly ContentPresenter _action = new() { HorizontalAlignment = HorizontalAlignment.Center };

    public EmptyState()
    {
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        var resources = Application.Current.Resources;
        _icon.Foreground = (Brush)resources["TextFillColorSecondaryBrush"];
        _title.Style = (Style)resources["TitleTextBlockStyle"];
        _subtitle.Style = (Style)resources["SubtitleTextBlockStyle"];
        _subtitle.Foreground = (Brush)resources["TextFillColorTertiaryBrush"];
        var panel = new StackPanel { Spacing = 16, ChildrenTransitions = [new EntranceThemeTransition { FromVerticalOffset = 20 }] };
        panel.Children.Add(_icon);
        panel.Children.Add(_title);
        panel.Children.Add(_subtitle);
        panel.Children.Add(_action);
        Content = panel;
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
        _icon.Glyph = hasError ? ErrorGlyph : Glyph;
        _title.Text = hasError ? Strings.Generic_Error : Title;
        _subtitle.Text = hasError ? Strings.Generic_LoadError : Subtitle;
        _action.Content = hasError ? null : Action;
        Visibility = IsShown || hasError ? Visibility.Visible : Visibility.Collapsed;
    }
}

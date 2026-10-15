using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.Presentation.ViewModels;

namespace Nagi.WinUI.Controls;

/// <summary>
///     The connect, finish, cancel and disconnect actions for a Last.fm account.
/// </summary>
public sealed partial class LastFmAccountButtons : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel),
        typeof(ScrobblingSettingsViewModel),
        typeof(LastFmAccountButtons),
        new PropertyMetadata(null));

    public LastFmAccountButtons()
    {
        InitializeComponent();
    }

    public ScrobblingSettingsViewModel? ViewModel
    {
        get => (ScrobblingSettingsViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }
}

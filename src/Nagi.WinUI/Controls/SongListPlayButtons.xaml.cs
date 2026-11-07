using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.Presentation.ViewModels;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.Controls;

/// <summary>
///     A song list's play all button and its shuffle button, whose menu offers the mixes.
/// </summary>
public sealed partial class SongListPlayButtons : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(SongListViewModelBase), typeof(SongListPlayButtons), new PropertyMetadata(null));

    public SongListPlayButtons()
    {
        InitializeComponent();
    }

    public SongListViewModelBase? ViewModel
    {
        get => (SongListViewModelBase?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private void MixMenu_Opening(object sender, object e) =>
        ListeningMenuFlyoutHelper.PopulateMixes((MenuFlyout)sender, ViewModel!.PlayMixCommand, ViewModel.PlayForCommand);
}

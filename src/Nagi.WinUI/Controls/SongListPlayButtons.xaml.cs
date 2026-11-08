using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.Presentation.Lists;
using Nagi.Presentation.Player;
using Nagi.Presentation.Resources;

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

    private void MixMenu_Opening(object sender, object e)
    {
        var menu = (MenuFlyout)sender;
        menu.Items.Clear();
        foreach (var mode in ShuffleModeText.MixModes)
        {
            var item = new MenuFlyoutItem
            {
                Text = ShuffleModeText.GetName(mode),
                Command = ViewModel!.PlayMixCommand,
                CommandParameter = mode
            };
            var description = ShuffleModeText.GetDescription(mode);
            if (description.Length > 0) ToolTipService.SetToolTip(item, description);
            menu.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = Strings.Listening_PlayFor, Command = ViewModel!.PlayForCommand });
    }
}

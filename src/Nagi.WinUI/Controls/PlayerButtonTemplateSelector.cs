using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.WinUI.Models;

namespace Nagi.WinUI.Controls;

/// <summary>
///     Selects the appropriate DataTemplate for a player control button based on its ID.
///     This allows a dynamic ItemsControl to render different buttons with unique styles and commands.
/// </summary>
public class PlayerButtonTemplateSelector : DataTemplateSelector
{
    public DataTemplate? ShuffleButtonTemplate { get; set; }
    public DataTemplate? PreviousButtonTemplate { get; set; }
    public DataTemplate? PlayPauseButtonTemplate { get; set; }
    public DataTemplate? NextButtonTemplate { get; set; }
    public DataTemplate? RepeatButtonTemplate { get; set; }
    public DataTemplate? LyricsButtonTemplate { get; set; }
    public DataTemplate? QueueButtonTemplate { get; set; }
    public DataTemplate? VolumeButtonTemplate { get; set; }

    /// <summary>
    ///     Returns a specific DataTemplate for a given PlayerButtonSetting.
    /// </summary>
    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
    {
        if (item is not PlayerButtonSetting buttonSetting) return base.SelectTemplateCore(item, container);

        return buttonSetting.Id switch
        {
            "Shuffle" => ShuffleButtonTemplate,
            "Previous" => PreviousButtonTemplate,
            "PlayPause" => PlayPauseButtonTemplate,
            "Next" => NextButtonTemplate,
            "Repeat" => RepeatButtonTemplate,
            "Lyrics" => LyricsButtonTemplate,
            "Queue" => QueueButtonTemplate,
            "Volume" => VolumeButtonTemplate,
            _ => base.SelectTemplateCore(item, container)
        };
    }
}
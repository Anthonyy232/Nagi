using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.WinUI.Models;

namespace Nagi.WinUI.Controls;

/// <summary>
///     Selects the specialized player buttons and the shared command button template.
/// </summary>
public class PlayerButtonTemplateSelector : DataTemplateSelector
{
    public DataTemplate? DefaultButtonTemplate { get; set; }
    public DataTemplate? PlayPauseButtonTemplate { get; set; }
    public DataTemplate? LyricsButtonTemplate { get; set; }
    public DataTemplate? QueueButtonTemplate { get; set; }
    public DataTemplate? ShuffleButtonTemplate { get; set; }
    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
    {
        if (item is not PlayerButtonSetting buttonSetting) return base.SelectTemplateCore(item, container);

        return buttonSetting.Id switch
        {
            "PlayPause" => PlayPauseButtonTemplate,
            "Lyrics" => LyricsButtonTemplate,
            "Queue" => QueueButtonTemplate,
            "Shuffle" => ShuffleButtonTemplate,
            _ => DefaultButtonTemplate
        };
    }
}
